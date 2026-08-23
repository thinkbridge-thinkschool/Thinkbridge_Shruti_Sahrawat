import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { requestTimeoutInterceptor } from './request-timeout';

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
    // The timeout interceptor bounds how long a request may hang. Without it a
    // request that never settles leaves the UI on "Loading…" forever, which is
    // exactly what happened when the dev proxy refused a connection and then
    // never answered. See request-timeout.ts.
    provideHttpClient(withFetch(), withInterceptors([requestTimeoutInterceptor])),
  ],
};
