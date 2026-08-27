import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { QuoteDetail } from './quote-detail';
import type { Quote } from './quotes';

/**
 * Exercises QuoteDetail — routed at `quotes/:id` — against a mocked
 * GET /api/quotes/{id}, driving it the way the router actually does: real
 * navigations through `RouterTestingHarness`, with `withComponentInputBinding()`
 * wired in exactly as it is in `app.config.ts`, rather than setting the `id`
 * input by hand. A test that set `fixture.componentRef.setInput('id', ...)`
 * directly would prove the component works when driven correctly and say
 * nothing about whether the *route* actually feeds it what it expects to
 * receive.
 *
 * Written once against the target behaviour, same convention as every prior
 * day's spec here: green against the fix, and — for the `parseQuoteId` cases
 * — red against the Day 16 draft, which sent `/api/quotes/NaN` to the API
 * for a non-numeric `:id` instead of catching it client-side.
 */
describe('QuoteDetail — GET /api/quotes/{id} (route-driven)', () => {
  let harness: RouterTestingHarness;
  let httpMock: HttpTestingController;

  async function settle(): Promise<void> {
    await Promise.resolve();
    await Promise.resolve();
    TestBed.tick();
  }

  async function navigate(id: string): Promise<QuoteDetail> {
    const component = await harness.navigateByUrl(`/quotes/${id}`, QuoteDetail);
    await settle();
    return component;
  }

  async function respond(
    request: TestRequest,
    body: object,
    opts?: { status: number; statusText: string },
  ): Promise<void> {
    if (!request.cancelled) {
      opts ? request.flush(body, opts) : request.flush(body);
    }
    await settle();
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'quotes/:id', component: QuoteDetail }], withComponentInputBinding()),
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => httpMock.verify({ ignoreCancelled: true }));

  const quote = (id: number): Quote => ({
    id,
    author: `Author ${id}`,
    text: `Quote text ${id}`,
    createdAt: '2026-03-14T09:30:00',
  });

  it('reports loading the instant the route activates, before the response arrives', async () => {
    const component = await navigate('1');

    expect(component.state()).toEqual({ status: 'loading' });

    await respond(httpMock.expectOne('/api/quotes/1'), quote(1));
  });

  it('reports ready with the fetched quote once the response arrives', async () => {
    const component = await navigate('1');
    await respond(httpMock.expectOne('/api/quotes/1'), quote(1));

    expect(component.state()).toEqual({ status: 'ready', quote: quote(1) });
  });

  it('surfaces a 404 as an error carrying the status code — not as swallowed emptiness', async () => {
    // A quote can legitimately 404: it was deleted (Day 1 supports DELETE
    // /api/quotes/{id}) between the list loading and the link being
    // followed, or the URL was typed/bookmarked directly with a stale id.
    const component = await navigate('999999');

    await respond(
      httpMock.expectOne('/api/quotes/999999'),
      { title: 'Quote not found', status: 404, detail: 'No quote with id 999999.' },
      { status: 404, statusText: 'Not Found' },
    );

    expect(component.state()).toEqual({ status: 'error', statusCode: 404 });
  });

  it('does not let a stale response for a previous :id overwrite the current one', async () => {
    // Navigate to quote 1, then — before its response arrives — navigate to
    // quote 2. Same matched route (`quotes/:id`), so the router reuses the
    // component instance and only the `id` input changes; the ordinary
    // "open one quote, go back, open another" sequence, not a contrived one.
    const component = await navigate('1');
    const requestForOne = httpMock.expectOne('/api/quotes/1');

    await navigate('2');
    const requestForTwo = httpMock.expectOne('/api/quotes/2');

    // The network answers out of order: quote 2 (the current route)
    // resolves first, and the now-superseded request for quote 1 resolves
    // after it.
    await respond(requestForTwo, quote(2));
    await respond(requestForOne, quote(1));

    // Whichever order the network answers in, the screen must show what the
    // URL currently says — httpResource cancels the superseded request
    // itself; nothing here has to track "is this response for the id I
    // still care about" by hand.
    expect(component.state()).toEqual({ status: 'ready', quote: quote(2) });
  });

  /**
   * `stray()` matches (and defuses) any request the implementation issued
   * anyway, *before* asserting on it — deliberately, rather than leaving a
   * failed assertion to abandon an unflushed `HttpTestingController` request
   * for `afterEach`'s `verify()` to trip over. Against the Day 16 draft
   * (`Number(this.id())`, no validation), that request is a real one —
   * `GET /api/quotes/NaN` — and an unflushed mock request left behind by a
   * failing assertion here was observed to corrupt the *next* test's
   * `TestBed.configureTestingModule()` (`teardown.destroyAfterEach` never
   * gets a clean run), not just this one's. Closing it here first keeps a
   * failure local to the test that actually failed.
   */
  function stray(): TestRequest[] {
    const requests = httpMock.match(() => true);
    requests.forEach((req) => req.flush({}, { status: 404, statusText: 'Not Found' }));
    return requests;
  }

  it('rejects a non-numeric :id without ever calling the API', async () => {
    const component = await navigate('abc');

    expect(stray()).toEqual([]);
    expect(component.state()).toEqual({ status: 'invalid', raw: 'abc' });
  });

  it('rejects a negative or zero :id the same way', async () => {
    const zero = await navigate('0');
    expect(stray()).toEqual([]);
    expect(zero.state()).toEqual({ status: 'invalid', raw: '0' });

    // A second navigation on the same harness reuses the same route/component.
    const negative = await navigate('-3');
    expect(stray()).toEqual([]);
    expect(negative.state()).toEqual({ status: 'invalid', raw: '-3' });
  });
});
