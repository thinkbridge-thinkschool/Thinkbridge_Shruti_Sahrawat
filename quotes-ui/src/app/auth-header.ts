import { HttpInterceptorFn } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';

/** The signed-in user, as /api/auth/me and the login response describe them. */
export interface AuthUser {
  id: number;
  email: string;
  role: 'user' | 'admin';
}

/**
 * Where the browser keeps the session between page loads.
 *
 * One key holding both halves, so a reload can never restore a token without
 * the user it belongs to (or the reverse) — two keys can be written or cleared
 * independently, and a token with no user means a signed-in app that cannot
 * say who is signed in.
 */
const STORAGE_KEY = 'quotes-ui.session';

/**
 * Holds the bearer token and who it belongs to.
 *
 * localStorage rather than memory alone, because memory alone means every
 * refresh — and every link opened in a new tab — signs the user out. The cost
 * is that the token is readable by any script running on this origin, so an
 * XSS bug becomes a token theft as well. That trade is the normal one for a
 * SPA calling its own API; the alternative that genuinely closes it is an
 * HttpOnly cookie, which this API does not issue and which would bring CSRF
 * protection along as its own new problem. What keeps the blast radius small
 * is the eight-hour expiry (see JwtOptions) — a stolen token is not a
 * permanent key to the account.
 *
 * sessionStorage was the other option: it would scope the token to one tab and
 * clear it on close, which is safer, and it would also sign the user out every
 * time they open a quote in a new tab.
 */
@Injectable({ providedIn: 'root' })
export class AuthTokenStore {
  /**
   * The live token. Written directly — by a test, say — this is session-only;
   * `persist()` is what also writes it down for the next page load. That
   * asymmetry is deliberate: a signal that wrote to storage on every set would
   * leak state between tests sharing one jsdom, and there is exactly one place
   * in the app that legitimately starts a durable session.
   */
  readonly token = signal<string | null>(null);

  readonly user = signal<AuthUser | null>(null);

  readonly isSignedIn = computed(() => this.token() !== null);

  readonly isAdmin = computed(() => this.user()?.role === 'admin');

  constructor() {
    this.restore();
  }

  /** Starts a durable session: signals set, and written down for next time. */
  persist(token: string, user: AuthUser): void {
    this.token.set(token);
    this.user.set(user);
    this.write(JSON.stringify({ token, user }));
  }

  /** Ends it, here and in storage. */
  clear(): void {
    this.token.set(null);
    this.user.set(null);
    this.write(null);
  }

  /**
   * Reads whatever the last session left behind.
   *
   * Anything unreadable is treated as no session rather than as an error. The
   * stored value can be absent, truncated, or left over from an older shape of
   * this app, and none of those should reach the user as a crash on startup —
   * the honest response to "I cannot tell who you are" is a login page.
   */
  private restore(): void {
    let raw: string | null = null;
    try {
      raw = localStorage.getItem(STORAGE_KEY);
    } catch {
      // Storage can throw outright, not merely return null: Safari's private
      // mode and a browser configured to block site data both do.
      return;
    }
    if (!raw) return;

    try {
      const parsed = JSON.parse(raw) as { token?: unknown; user?: unknown };
      const token = typeof parsed.token === 'string' ? parsed.token : null;
      const user = parsed.user as AuthUser | undefined;

      if (!token || !user || typeof user.email !== 'string' || typeof user.id !== 'number') {
        this.write(null);
        return;
      }

      this.token.set(token);
      this.user.set(user);
    } catch {
      this.write(null);
    }
  }

  private write(value: string | null): void {
    try {
      if (value === null) localStorage.removeItem(STORAGE_KEY);
      else localStorage.setItem(STORAGE_KEY, value);
    } catch {
      // Quota exceeded, or storage blocked. The in-memory signals are already
      // set, so this session works — it just will not survive a reload. Not
      // worth failing a sign-in over.
    }
  }
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
 * Silent no-op with no token set, which is the state the app is in before
 * anyone signs in — so the login and register calls themselves, which are the
 * only requests made from that state, go out unadorned.
 */
export const authHeaderInterceptor: HttpInterceptorFn = (req, next) => {
  const token = inject(AuthTokenStore).token();

  if (!token || !isSameOrigin(req.url)) {
    return next(req);
  }

  return next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));
};
