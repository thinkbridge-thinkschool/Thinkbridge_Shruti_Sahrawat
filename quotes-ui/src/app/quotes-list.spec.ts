import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { QuotesList } from './quotes-list';

/**
 * Renders QuotesList through the router, the way the browser actually does —
 * not QuotesStore in isolation. quotes-store.spec.ts proves the store's
 * signals no longer throw on a 401; this proves the thing a user actually
 * sees is the "session expired" branch quotes-list.ts already has, not a
 * skeleton stuck forever because the template's unconditional pager
 * (`store.totalCount()`, outside the `@switch`) threw before that branch
 * ever rendered.
 */
describe('QuotesList — session-expired rendering', () => {
  let harness: RouterTestingHarness;
  let httpMock: HttpTestingController;

  async function settle(): Promise<void> {
    await Promise.resolve();
    await Promise.resolve();
    TestBed.tick();
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'quotes', component: QuotesList }]),
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => httpMock.verify());

  it('renders "Sign in again", not a stuck skeleton, when the list request 401s', async () => {
    await harness.navigateByUrl('/quotes', QuotesList);
    await settle();

    httpMock
      .expectOne((r) => r.url.startsWith('/api/quotes?'))
      .flush({ title: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });
    await settle();

    const root = harness.routeNativeElement!;

    // Rendering got this far at all is part of what's under test: against
    // the unguarded store, the pager's unconditional store.totalCount() read
    // threw during this same change-detection pass, and root's content below
    // would still be whatever the last successful render left behind (here,
    // the loading skeleton) rather than the error branch.
    expect(root.querySelector('.skeleton-list')).toBeNull();
    expect(root.textContent).toContain('Your session has expired');

    const signInAgain = Array.from(root.querySelectorAll('button')).find(
      (button) => button.textContent?.trim() === 'Sign in again',
    );
    expect(signInAgain).toBeTruthy();
  });
});
