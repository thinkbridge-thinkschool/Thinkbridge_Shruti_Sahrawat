import { HttpInterceptorFn } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';

/**
 * Holds whatever bearer token the app currently has, if any.
 *
 * There is no login flow in this app and no live Week-1 API endpoint that
 * checks an Authorization header today — confirmed by reading
 * QuotesApi/Program.cs, which calls no `AddAuthentication`/`AddAuthorization`
 * at all. So this is deliberately a stub: a signal an eventual auth flow
 * would set, not a working integration. Building the interceptor now, ahead
 * of that flow existing, is still worth doing — it is the layer the request
 * has to pass through regardless of what fills the token in later, and
 * writing it against a stub keeps that layer testable without a live login
 * page.
 */
@Injectable({ providedIn: 'root' })
export class AuthTokenStore {
  readonly token = signal<string | null>(null);
}

/**
 * The origins this interceptor is willing to attach a token to.
 *
 * Same-origin only, by relative-URL default — `req.url` for every call this
 * app makes today is a path like `/api/quotes`, which `URL` resolves against
 * `location.origin` when given no base of its own. An interceptor that
 * attaches the token to *every* outgoing request regardless of destination
 * is a real, common mistake: the day this app calls a third-party API (a
 * font host, a payment provider, an analytics endpoint) over HttpClient
 * rather than a plain `<link>` tag, an unscoped interceptor hands that third
 * party this app's own bearer token. Scoping to same-origin now costs
 * nothing and means nobody has to remember to add the check later, under
 * time pressure, after the leak already shipped.
 */
function isSameOrigin(url: string): boolean {
  try {
    return new URL(url, location.origin).origin === location.origin;
  } catch {
    // A URL the platform itself cannot parse is not one to attach a
    // credential to.
    return false;
  }
}

/**
 * Attaches `Authorization: Bearer <token>` to same-origin requests, when a
 * token is present.
 *
 * Silent no-op with no token set — which is the only state this app is
 * actually in today, so every existing request is unaffected until an auth
 * flow calls `AuthTokenStore.token.set(...)`.
 */
export const authHeaderInterceptor: HttpInterceptorFn = (req, next) => {
  const token = inject(AuthTokenStore).token();

  if (!token || !isSameOrigin(req.url)) {
    return next(req);
  }

  return next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));
};
