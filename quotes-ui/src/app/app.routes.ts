import { Routes } from '@angular/router';
import { authGuard } from './auth-guard';

/**
 * Every feature route is lazy via `loadComponent`, none via a top-level
 * import — QuotesList, QuoteDetail, QuoteForm and LoginPage each land in
 * their own chunk, fetched only when the router actually navigates there,
 * not up front alongside `app-root`. `npm run build`'s output is what
 * proves this, not a guess: see VERIFICATION-ROUTING.md for the specific
 * "Lazy chunk files" lines each one produces.
 *
 * `:id` is bound straight into `QuoteDetail`'s `id` input by
 * `withComponentInputBinding()` (`app.config.ts`) — no `ActivatedRoute`
 * plumbing inside the component itself. Angular's router draws no
 * equivalent of the server's `{id:int}` route constraint: *any* string in
 * that segment matches and reaches the component. `quote-id.ts` is where
 * that gets validated, once, before a request is ever built from it.
 */
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'quotes' },
  {
    path: 'quotes',
    loadComponent: () => import('./quotes-list').then((m) => m.QuotesList),
    title: 'Quotes',
  },
  {
    // Guarded — see auth-guard.ts for why a route the real API doesn't
    // itself protect is still worth guarding client-side.
    path: 'quotes/new',
    loadComponent: () => import('./quote-form').then((m) => m.QuoteForm),
    canActivate: [authGuard],
    title: 'Add a quote',
  },
  {
    path: 'quotes/:id',
    loadComponent: () => import('./quote-detail').then((m) => m.QuoteDetail),
    title: 'Quote detail',
  },
  {
    path: 'login',
    loadComponent: () => import('./login-page').then((m) => m.LoginPage),
    title: 'Sign in',
  },
  // Catches anything else — a typo'd path, a link to a route this app used
  // to have — and sends it back to the list rather than Angular's default
  // blank screen for an unmatched URL.
  { path: '**', redirectTo: 'quotes' },
];
