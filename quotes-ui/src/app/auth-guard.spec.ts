import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  Router,
  RouterStateSnapshot,
  UrlTree,
  provideRouter,
} from '@angular/router';
import { authGuard } from './auth-guard';
import { AuthTokenStore } from './auth-header';

/**
 * Unit-level: calls `authGuard` directly, inside an injection context, the
 * same way the router itself does — rather than only through a full
 * navigation (that integration-level check lives in `app.routes.spec.ts`,
 * which confirms the *end-to-end* redirect actually lands on `/login`). This
 * file is what asserts the shape of what the guard returns: a `UrlTree`
 * carrying `redirectTo`, not merely a falsy value.
 */
describe('authGuard', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
  });

  // Only `state.url` is read by the guard; the rest of a real
  // RouterStateSnapshot/ActivatedRouteSnapshot is irrelevant here.
  const stateAt = (url: string) => ({ url }) as RouterStateSnapshot;
  const route = {} as ActivatedRouteSnapshot;

  it('allows the navigation through when a token is set', () => {
    TestBed.inject(AuthTokenStore).token.set('demo-token');

    const result = TestBed.runInInjectionContext(() => authGuard(route, stateAt('/quotes/new')));

    expect(result).toBe(true);
  });

  it('redirects to /login with the original URL when no token is set', () => {
    expect(TestBed.inject(AuthTokenStore).token()).toBeNull();

    const result = TestBed.runInInjectionContext(() => authGuard(route, stateAt('/quotes/new')));

    expect(result).toBeInstanceOf(UrlTree);
    const tree = result as UrlTree;
    expect(tree.toString()).toBe(TestBed.inject(Router).createUrlTree(
      ['/login'],
      { queryParams: { redirectTo: '/quotes/new' } },
    ).toString());
    expect(tree.queryParams['redirectTo']).toBe('/quotes/new');
  });
});
