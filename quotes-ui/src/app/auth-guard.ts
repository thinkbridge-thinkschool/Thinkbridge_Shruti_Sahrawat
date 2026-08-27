import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthTokenStore } from './auth-header';

/**
 * Guards `/quotes/new`. Creating a quote is treated as an action that needs
 * a signed-in user — client-side policy, not something the real Week-1 API
 * enforces today. `QuotesApi/Program.cs` calls no
 * `AddAuthentication`/`AddAuthorization` at all (confirmed on Day 15, in
 * `auth-header.ts`'s own write-up), so nothing on the server would reject an
 * anonymous `POST /api/quotes`. This guard exists anyway, for the same
 * reason `authHeaderInterceptor` does: it is the layer a request has to pass
 * through regardless of what eventually fills `AuthTokenStore.token()` in,
 * and writing it against the existing stub keeps it testable — and this
 * exercise's guard requirement demonstrable — without a real login flow
 * existing yet.
 *
 * Returns a `UrlTree`, not `false`. A guard that returns `false` cancels the
 * navigation and stops there — the address bar can be left pointing at a URL
 * that never actually rendered, and getting the user somewhere useful means
 * a second, separate `router.navigate()` call racing the guard's own return.
 * A returned `UrlTree` is treated as "cancel this navigation, replace it
 * with this one" as a single atomic step, which is the redirect this guard
 * is actually for.
 *
 * `redirectTo` rides along as a query param so `LoginPage` can send the user
 * on to what they originally asked for, rather than always landing back on
 * `/quotes` regardless of where the guard caught them.
 */
export const authGuard: CanActivateFn = (_route, state) => {
  const token = inject(AuthTokenStore).token();
  if (token) return true;

  return inject(Router).createUrlTree(['/login'], {
    queryParams: { redirectTo: state.url },
  });
};
