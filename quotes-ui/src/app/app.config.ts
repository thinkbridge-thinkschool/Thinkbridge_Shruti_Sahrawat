import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding, withViewTransitions } from '@angular/router';
import { authHeaderInterceptor } from './auth-header';
import { errorMappingInterceptor } from './error-mapping';
import { requestTimeoutInterceptor } from './request-timeout';
import { retryWithBackoffInterceptor } from './retry-backoff';
import { routes } from './app.routes';

/**
 * Note what is absent: provideZonelessChangeDetection().
 *
 * Angular 21 is zoneless by default — there is no zone.js in the bundle and no
 * provider to add. The opposite is now the explicit choice: an app that still
 * wants Zone.js has to ask for provideZoneChangeDetection().
 *
 * What that changes, concretely: nothing monkey-patches setTimeout, addEventListener
 * or XMLHttpRequest any more, so Angular has no way to be told "something might
 * have happened, re-check everything". Change detection is driven by signals
 * instead — a component is checked when a signal it read has actually changed.
 * Mutating a plain object field and expecting the view to notice does not work.
 * That is why every piece of state on this screen is a signal.
 */
export const appConfig: ApplicationConfig = {
  providers: [
    // Surfaces uncaught errors and unhandled rejections through Angular's
    // error handler rather than letting them die silently in the console.
    provideBrowserGlobalErrorListeners(),

    // withFetch uses the Fetch API rather than XMLHttpRequest. Worth being
    // deliberate about in a zoneless app: XHR was one of the things zone.js
    // used to patch, and fetch is the modern path httpResource is built for.
    //
    // Order matters here — each interceptor wraps everything after it, so
    // this list reads outside-in for the request and inside-out for the
    // response:
    //
    //   authHeaderInterceptor    — outermost: attaches a header, nothing else.
    //   errorMappingInterceptor  — sees the *final* error, after retries are
    //                              exhausted, so it maps once, not per attempt.
    //   retryWithBackoffInterceptor — wraps every individual attempt below it,
    //                              including the timeout on each one.
    //   requestTimeoutInterceptor — innermost: bounds a single attempt, not
    //                              the whole retried sequence.
    //
    // Swapping retry and errorMapping would mean errorMapping runs on every
    // attempt instead of once at the end, and would need to unwrap its own
    // AppError back into something retry's status check understands — worth
    // writing down since the order is load-bearing, not stylistic.
    provideHttpClient(
      withFetch(),
      withInterceptors([
        authHeaderInterceptor,
        errorMappingInterceptor,
        retryWithBackoffInterceptor,
        requestTimeoutInterceptor,
      ]),
    ),

    // withComponentInputBinding() binds a route's params (and query params,
    // and data) directly onto a matching component input signal — QuoteDetail
    // declares `id = input.required<string>()` and never touches
    // ActivatedRoute itself. Without this, every routed component that wants
    // its own route param goes back to injecting ActivatedRoute and
    // subscribing by hand, the exact kind of manual subscription this app has
    // spent Days 13-15 replacing with signals wherever an httpResource or a
    // linkedSignal could do it declaratively instead.
    //
    // withViewTransitions() wraps every navigation in the browser's native
    // View Transitions API when the browser supports one (Chromium; a no-op,
    // not a break, elsewhere) — list -> detail, detail -> list, and list ->
    // the create form all get it for free, rather than only the one
    // list/detail pair hand-wired with document.startViewTransition() calls
    // sprinkled through click handlers.
    provideRouter(routes, withComponentInputBinding(), withViewTransitions()),
  ],
};
