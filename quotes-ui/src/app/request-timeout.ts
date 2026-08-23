import { HttpInterceptorFn } from '@angular/common/http';
import { timeout } from 'rxjs';

/**
 * How long any single request may hang before it is treated as failed.
 *
 * Generous — this is a backstop against a request that will never settle, not
 * a performance budget. A local API answering this endpoint takes single-digit
 * milliseconds.
 */
export const REQUEST_TIMEOUT_MS = 10_000;

/**
 * Fails any request that never settles.
 *
 * Added after observing a screen stuck on "Loading quotes…" indefinitely. The
 * API was stopped, the dev-server proxy refused the connection and threw
 * (`AggregateError [ECONNREFUSED]`), and — having already begun handling the
 * request — never sent a response. The fetch never settled, so httpResource
 * never left `loading`, so the error branch never rendered. The failure was
 * real, immediate, and completely invisible to the user.
 *
 * That particular cause is a dev-server artifact. The class of failure is not:
 * a proxy that dies mid-request, a load balancer that drops a connection, or a
 * server that accepts and then never answers all look identical from the
 * browser — a promise that stays pending. Nothing in HttpClient bounds that by
 * default.
 *
 * The interceptor is the right layer for it. Putting a timeout in the component
 * would mean repeating it at every call site and forgetting it at one of them.
 */
export const requestTimeoutInterceptor: HttpInterceptorFn = (req, next) =>
  next(req).pipe(timeout({ each: REQUEST_TIMEOUT_MS }));
