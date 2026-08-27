import { HttpContextToken, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';

/**
 * Opts a single request into error mapping. Default `false` — most requests
 * in this app go through `httpResource` (the list and the detail page), and
 * `httpResource` already draws its own error distinction via `statusCode()`
 * / `error()`, which `QuotesList.failureKind` and `QuoteDetail`'s own
 * `DetailPageState` are built around. Rewriting the thrown error's shape for
 * *every* request would silently break both of those without touching a
 * line in either component — `httpResource`'s own status extraction expects
 * an `HttpErrorResponse`, not this interceptor's `AppError`.
 *
 * So this is opt-in per request rather than global. `QuotesStore.createQuote`
 * sets it, because that call site already does its own by-hand status
 * parsing (see quotes-api.ts) and is a direct, natural place to have the
 * interceptor do that classification instead.
 */
export const MAP_ERRORS = new HttpContextToken<boolean>(() => false);

/**
 * A friendly-surface error, replacing the raw `HttpErrorResponse` for any
 * request that opts in via `MAP_ERRORS`.
 *
 * `message` is what a user can be shown directly. Anything that needs the
 * original response — logging, a "details" disclosure — stays reachable
 * through `cause`, deliberately typed `unknown` so nothing downstream is
 * tempted to lean on `HttpErrorResponse`-specific fields and reintroduce the
 * coupling this type exists to remove.
 */
export type AppError =
  | { kind: 'validation'; statusCode: 400; message: string; fieldErrors: Record<string, string[]>; cause: unknown }
  | { kind: 'notFound'; statusCode: 404; message: string; cause: unknown }
  | { kind: 'client'; statusCode: number; message: string; cause: unknown }
  | { kind: 'server'; statusCode: number; message: string; cause: unknown }
  | { kind: 'network'; statusCode?: undefined; message: string; cause: unknown };

/**
 * Classifies a failed request against this API's real 4xx shapes.
 *
 * Two distinct real shapes, not one — checked against
 * `QuotesApi/Extensions/EndpointExtensions.cs`, not assumed:
 *
 * - `POST /api/quotes` with an invalid body returns `Results.ValidationProblem(errors)`
 *   — a `ValidationProblemDetails`, `errors` a dictionary keyed by the C#
 *   property name (`"Author"`, capitalised — see quote-form.ts for why).
 * - `GET /api/quotes/{id}` for a missing id returns a hand-built
 *   `Results.NotFound(new ProblemDetails { Title, Status, Detail })` — no
 *   `errors` dictionary at all, and its own fixed fields (`title`, `status`,
 *   `detail`) *do* serialise camelCase, unlike the dynamic dictionary keys
 *   on the 400. Treating every 4xx as "the same shape, different status"
 *   would read `problem.errors` on a 404 and get `undefined` — which this
 *   function treats as "no field errors", not as a parse failure.
 */
function toAppError(error: unknown): AppError {
  if (!(error instanceof HttpErrorResponse)) {
    return {
      kind: 'network',
      message: 'Something went wrong before the request could be sent. Try again.',
      cause: error,
    };
  }

  if (error.status === 0) {
    // No status at all: the request never reached a server — refused
    // connection, DNS failure, offline. Distinct from a 5xx, where a server
    // was reached and it was the one that failed.
    return {
      kind: 'network',
      message: 'Could not reach the server. Check your connection and try again.',
      cause: error,
    };
  }

  if (error.status === 400) {
    const problem = error.error as { errors?: Record<string, string[]> } | null;
    return {
      kind: 'validation',
      statusCode: 400,
      message: 'Please fix the highlighted fields and try again.',
      fieldErrors: problem?.errors ?? {},
      cause: error,
    };
  }

  if (error.status === 404) {
    const problem = error.error as { detail?: string } | null;
    return {
      kind: 'notFound',
      statusCode: 404,
      message: problem?.detail ?? 'That could not be found.',
      cause: error,
    };
  }

  if (error.status >= 400 && error.status < 500) {
    return {
      kind: 'client',
      statusCode: error.status,
      message: 'That request could not be completed.',
      cause: error,
    };
  }

  return {
    kind: 'server',
    statusCode: error.status,
    message: 'The server ran into a problem. Try again in a moment.',
    cause: error,
  };
}

/**
 * Rewrites a failed response into an `AppError`, for requests that ask for
 * it via `MAP_ERRORS`. Every other request passes through untouched — see
 * the token's own comment for why this is opt-in rather than global.
 */
export const errorMappingInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.context.get(MAP_ERRORS)) {
    return next(req);
  }

  return next(req).pipe(catchError((error: unknown) => throwError(() => toAppError(error))));
};
