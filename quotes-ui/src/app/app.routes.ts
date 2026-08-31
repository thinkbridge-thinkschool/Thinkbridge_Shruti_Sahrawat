import { Routes } from '@angular/router';
import { authGuard } from './auth-guard';

/**
 * Every feature route is lazy via `loadComponent`, none via a top-level
 * import — QuotesList, QuoteDetail, QuoteForm, LoginPage and RegisterPage
 * each land in their own chunk, fetched only when the router actually
 * navigates there, not up front alongside `app-root`. `npm run build`'s
 * output is what proves this, not a guess: see VERIFICATION-ROUTING.md for
 * the specific "Lazy chunk files" lines each one produces.
 *
 * `:id` is bound straight into `QuoteDetail`'s `id` input by
 * `withComponentInputBinding()` (`app.config.ts`) — no `ActivatedRoute`
 * plumbing inside the component itself. Angular's router draws no
 * equivalent of the server's `{id:int}` route constraint: *any* string in
 * that segment matches and reaches the component. `quote-id.ts` is where
 * that gets validated, once, before a request is ever built from it.
 *
 * As of Day 19 every quotes route is guarded, not just `quotes/new`. That is
 * not the guard doing more than it used to — it is the guard finally matching
 * the server. `/api/quotes` requires a token now, so an unguarded list route
 * would render a page whose only possible content is a 401 error box. The
 * guard turns that into a sign-in prompt that remembers where the visitor was
 * heading.
 */
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'quotes' },
  {
    path: 'quotes',
    loadComponent: () => import('./quotes-list').then((m) => m.QuotesList),
    canActivate: [authGuard],
    title: 'Quotes',
  },
  {
    path: 'quotes/new',
    loadComponent: () => import('./quote-form').then((m) => m.QuoteForm),
    canActivate: [authGuard],
    title: 'Add a quote',
  },
  {
    path: 'quotes/:id',
    loadComponent: () => import('./quote-detail').then((m) => m.QuoteDetail),
    canActivate: [authGuard],
    title: 'Quote detail',
  },
  {
    path: 'login',
    loadComponent: () => import('./login-page').then((m) => m.LoginPage),
    title: 'Sign in',
  },
  {
    path: 'register',
    loadComponent: () => import('./register-page').then((m) => m.RegisterPage),
    title: 'Create an account',
  },
  // Catches anything else — a typo'd path, a link to a route this app used
  // to have — and sends it back to the list rather than Angular's default
  // blank screen for an unmatched URL. Unauthenticated, that lands on the
  // guarded list route and redirects on to /login, which is the right place
  // for a stranger following a broken link to end up.
  { path: '**', redirectTo: 'quotes' },
];
