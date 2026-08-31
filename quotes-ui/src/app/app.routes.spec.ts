import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from './app.routes';
import { QuotesList } from './quotes-list';
import { QuoteDetail } from './quote-detail';
import { QuoteForm } from './quote-form';
import { LoginPage } from './login-page';
import { AuthTokenStore } from './auth-header';

/**
 * Exercises the real route table from `app.routes.ts` — the same one
 * `app.config.ts` wires into `provideRouter()` — through actual navigations,
 * not a hand-picked test route table. This is the integration-level check
 * for two of this exercise's specific asks: that the guard really redirects
 * an unauthenticated navigation, and that an unmatched URL doesn't dead-end
 * on Angular's default blank screen.
 *
 * As of Day 19 every quotes route is guarded, so the tests that expect a
 * quotes page to render sign in first — `signedIn()`. That is not the tests
 * working around the guard: it is them describing the app as it now is, where
 * reaching any quotes screen without a token is the exception rather than the
 * default. The two tests that assert the redirect deliberately do not call
 * it.
 *
 * Lazy loading itself (loadComponent's separate chunks) is not something a
 * unit test can observe — a bundler concern, not a runtime one — so it's
 * verified separately, against `npm run build`'s own output. See
 * VERIFICATION-ROUTING.md.
 */
describe('app.routes — the real route table', () => {
  let harness: RouterTestingHarness;
  let httpMock: HttpTestingController;

  async function settle(): Promise<void> {
    await Promise.resolve();
    await Promise.resolve();
    TestBed.tick();
  }

  /**
   * Puts a token in the store, which is all `authGuard` reads.
   *
   * Not a real sign-in through AuthService: the guard's question is "is there
   * a token", and routing this through an HTTP round trip would make every
   * routing test depend on the shape of the login response as well.
   */
  function signedIn(): void {
    TestBed.inject(AuthTokenStore).token.set('test-token');
  }

  function flushList(): void {
    httpMock
      .expectOne((req) => req.url.startsWith('/api/quotes?'))
      .flush({ items: [], page: 1, size: 10, totalCount: 0 });
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter(routes, withComponentInputBinding()),
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => httpMock.verify({ ignoreCancelled: true }));

  it('redirects the empty path to the quotes list', async () => {
    signedIn();

    const component = await harness.navigateByUrl('/', QuotesList);
    await settle();

    expect(component).toBeInstanceOf(QuotesList);
    flushList();
    await settle();
  });

  it('redirects an unmatched path to the quotes list, instead of a blank screen', async () => {
    signedIn();

    const component = await harness.navigateByUrl('/nonsense/nowhere', QuotesList);
    await settle();

    expect(component).toBeInstanceOf(QuotesList);
    flushList();
    await settle();
  });

  it('routes quotes/:id to QuoteDetail with the id bound straight in', async () => {
    signedIn();

    const component = await harness.navigateByUrl('/quotes/42', QuoteDetail);
    await settle();

    httpMock
      .expectOne('/api/quotes/42')
      .flush({ id: 42, author: 'Ada Lovelace', text: 'Q', createdAt: '2026-01-01T00:00:00', ownerId: 3 });
    await settle();

    expect(component.state()).toEqual({
      status: 'ready',
      quote: { id: 42, author: 'Ada Lovelace', text: 'Q', createdAt: '2026-01-01T00:00:00', ownerId: 3 },
    });
  });

  it('sends an unauthenticated visit to quotes/new to /login instead', async () => {
    expect(TestBed.inject(AuthTokenStore).token()).toBeNull();

    const component = await harness.navigateByUrl('/quotes/new', LoginPage);
    await settle();

    expect(component).toBeInstanceOf(LoginPage);
    // QuoteForm never got created — no list resource fetch, nothing for
    // httpMock.verify() to find outstanding in afterEach.
  });

  it('sends an unauthenticated visit to the quotes list itself to /login', async () => {
    // New on Day 19. Before quotes had owners the list was public, because
    // GET /api/quotes was; now that the endpoint requires a token, an
    // unguarded list route could only ever render a 401 error box.
    expect(TestBed.inject(AuthTokenStore).token()).toBeNull();

    const component = await harness.navigateByUrl('/quotes', LoginPage);
    await settle();

    expect(component).toBeInstanceOf(LoginPage);
  });

  it('lets quotes/new through once a token is set', async () => {
    signedIn();

    const component = await harness.navigateByUrl('/quotes/new', QuoteForm);
    await settle();

    expect(component).toBeInstanceOf(QuoteForm);
    flushList();
    await settle();
  });
});
