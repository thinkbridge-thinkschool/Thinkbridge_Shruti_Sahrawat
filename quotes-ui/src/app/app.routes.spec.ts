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
    const component = await harness.navigateByUrl('/', QuotesList);
    await settle();

    expect(component).toBeInstanceOf(QuotesList);
    flushList();
    await settle();
  });

  it('redirects an unmatched path to the quotes list, instead of a blank screen', async () => {
    const component = await harness.navigateByUrl('/nonsense/nowhere', QuotesList);
    await settle();

    expect(component).toBeInstanceOf(QuotesList);
    flushList();
    await settle();
  });

  it('routes quotes/:id to QuoteDetail with the id bound straight in', async () => {
    const component = await harness.navigateByUrl('/quotes/42', QuoteDetail);
    await settle();

    httpMock
      .expectOne('/api/quotes/42')
      .flush({ id: 42, author: 'Ada Lovelace', text: 'Q', createdAt: '2026-01-01T00:00:00' });
    await settle();

    expect(component.state()).toEqual({
      status: 'ready',
      quote: { id: 42, author: 'Ada Lovelace', text: 'Q', createdAt: '2026-01-01T00:00:00' },
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

  it('lets quotes/new through once a token is set', async () => {
    TestBed.inject(AuthTokenStore).token.set('demo-token');

    const component = await harness.navigateByUrl('/quotes/new', QuoteForm);
    await settle();

    expect(component).toBeInstanceOf(QuoteForm);
    flushList();
    await settle();
  });
});
