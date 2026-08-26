import { HttpInterceptorFn } from '@angular/common/http';
import { retry, timer } from 'rxjs';

const MAX_RETRIES = 3;
const BASE_DELAY_MS = 300;

/** Exponential backoff: 300ms, 600ms, 1200ms. */
function backoffDelay(retryCount: number) {
  return timer(BASE_DELAY_MS * 2 ** (retryCount - 1));
}

/** Retries a failed request with an increasing delay between attempts. */
export const retryWithBackoffInterceptor: HttpInterceptorFn = (req, next) =>
  next(req).pipe(retry({ count: MAX_RETRIES, delay: backoffDelay }));
