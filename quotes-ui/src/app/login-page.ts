import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthTokenStore } from './auth-header';

/**
 * Where `authGuard` sends a navigation it rejected. There is no real account
 * system behind this — the Week 1 API checks no `Authorization` header
 * today (see `auth-header.ts`) — and this page says so rather than
 * pretending to check a password against a server that has no concept of
 * one. It exists so the guard's redirect has somewhere real to land and
 * come back from: without it, "confirm the guard redirects when
 * unauthenticated" would have nothing to click through, only a route that
 * 404s.
 */
@Component({
  selector: 'app-login-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: `
    :host {
      display: block;
      max-width: 32rem;
      margin: 4rem auto;
      padding: 0 1.25rem;
      text-align: center;
      color: var(--ink);
    }

    h1 {
      margin: 0 0 1rem;
      font-size: 1.75rem;
      font-weight: 700;
      letter-spacing: -0.02em;
    }

    p {
      color: var(--muted);
      line-height: 1.6;
    }

    code {
      font-family: var(--font-mono);
      font-size: 0.86em;
    }

    button {
      margin-top: 1rem;
      padding: 0.65rem 1.5rem;
      border: none;
      border-radius: var(--radius-sm, 8px);
      background: var(--accent);
      color: var(--on-accent, white);
      font: inherit;
      font-weight: 600;
      cursor: pointer;
      box-shadow: var(--shadow-sm);
      transition: background-color 0.15s ease, box-shadow 0.15s ease, transform 0.15s ease;
    }

    button:hover {
      background: var(--accent-strong);
      box-shadow: var(--shadow-md);
      transform: translateY(-1px);
    }

    button:focus-visible {
      outline: 2px solid var(--accent);
      outline-offset: 2px;
    }
  `,
  template: `
    <h1>Sign in</h1>
    <p>
      There is no account system here — the Week&nbsp;1 API accepts every request
      with no <code>Authorization</code> header at all. This page stands in for a
      real one so <code>authGuard</code> has somewhere to send an unauthenticated
      visitor, and somewhere to send them back to once they are "signed in".
    </p>
    <button type="button" (click)="continue()">Continue as a demo user</button>
  `,
})
export class LoginPage {
  private readonly tokenStore = inject(AuthTokenStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  continue(): void {
    this.tokenStore.token.set('demo-token');
    const redirectTo = this.route.snapshot.queryParamMap.get('redirectTo');
    this.router.navigateByUrl(redirectTo || '/quotes');
  }
}
