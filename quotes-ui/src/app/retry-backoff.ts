import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { retry, throwError, timer } from 'rxjs';

const MAX_RETRIES = 3;
const BASE_DELAY_MS = 300;

/**
 * Only a transient failure is worth retrying: no response at all (status 0
 * — a dropped connection, a dead proxy) or a 5xx, where the server itself
 * failed. A 4xx means the server was reached and rejected this specific
 * request; sending the identical request again gets the identical
 * rejection, at the cost of a round-trip and a delay before the real
 * failure — the friendly message from errorMappingInterceptor, or a 4xx a
 * caller reads via statusCode() — reaches the app.
 */
function isTransient(error: unknown): boolean {
  return error instanceof HttpErrorResponse && (error.status === 0 || error.status >= 500);
}

/**
 * Exponential backoff: 300ms, 600ms, 1200ms.
 *
 * rxjs's `retry({ delay })` callback is `(error, retryCount) => ...` —
 * error first. A same-arity mistake here (treating the first argument as
 * the retry count) silently computes `BASE_DELAY_MS * 2 ** (error - 1)`:
 * arithmetic on an `HttpErrorResponse` object is `NaN`, and a `setTimeout`
 * scheduled for `NaN` runs on the next tick, not after any real delay —
 * which reads as "it retried" in a quick manual check and is actually "it
 * retried immediately, every time, with no backoff at all." Caught by
 * `retry-backoff.spec.ts`'s fake-timer assertion that nothing has retried
 * yet after 1ms, not by inspection.
 */
function backoffDelay(error: unknown, retryCount: number) {
  if (!isTransient(error)) {
    // Not the kind of failure another attempt can fix — abort the retry
    // sequence and let the original error through unchanged.
    return throwError(() => error);
  }
  return timer(BASE_DELAY_MS * 2 ** (retryCount - 1));
}

/**
 * Retries a failed idempotent GET with an increasing delay between
 * attempts. Method-scoped rather than applied to every request: retrying a
 * failed POST can create the quote twice, because a lost *response* to a
 * request the server actually processed is indistinguishable, from the
 * client, from the request never having arrived at all — and only GET is
 * safe to repeat without knowing which case this was.
 */
export const retryWithBackoffInterceptor: HttpInterceptorFn = (req, next) => {
  if (req.method !== 'GET') {
    return next(req);
  }

  return next(req).pipe(retry({ count: MAX_RETRIES, delay: backoffDelay }));
};
