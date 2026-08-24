import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { QuotesApi } from './quotes-api';
import type { Quote } from './quotes';

/**
 * Exercises QuotesApi.selectQuote()/detailState() — the surface QuoteDetail
 * and the list's row-highlighting both depend on — against a mocked
 * GET /api/quotes/{id}, without a running QuotesApi backend.
 *
 * This file is written once against the target behaviour and is meant to
 * run unchanged against either implementation behind detailState(): the
 * point of the façade in QuotesApi is that these tests do not know or care
 * whether the fetch underneath is an httpResource or a raw subscription.
 */
describe('QuotesApi — quote detail (GET /api/quotes/{id})', () => {
  let api: QuotesApi;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(QuotesApi);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  const quote = (id: number): Quote => ({
    id,
    author: `Author ${id}`,
    text: `Quote text ${id}`,
    createdAt: '2026-03-14T09:30:00',
  });

  it('is idle with nothing selected', () => {
    expect(api.detailState()).toEqual({ status: 'idle' });
    expect(api.selectedId()).toBeNull();
  });

  it('reports loading the instant a quote is selected, before the response arrives', () => {
    api.selectQuote(1);

    expect(api.detailState()).toEqual({ status: 'loading' });
    expect(api.selectedId()).toBe(1);

    httpMock.expectOne('/api/quotes/1').flush(quote(1));
  });

  it('reports ready with the fetched quote once the response arrives', () => {
    api.selectQuote(1);
    httpMock.expectOne('/api/quotes/1').flush(quote(1));

    expect(api.detailState()).toEqual({ status: 'ready', quote: quote(1) });
  });

  it('returns to idle when the selection is cleared', () => {
    api.selectQuote(1);
    httpMock.expectOne('/api/quotes/1').flush(quote(1));
    expect(api.detailState().status).toBe('ready');

    api.selectQuote(null);

    expect(api.detailState()).toEqual({ status: 'idle' });
    expect(api.selectedId()).toBeNull();
  });

  it('surfaces a 404 as an error carrying the status code — not as swallowed emptiness', () => {
    // A quote can legitimately 404: it was deleted (Day 1 supports DELETE
    // /api/quotes/{id}) between the list loading and the row being clicked.
    // That is a real, distinct condition from "nothing selected yet", and
    // from a 500 or a dead proxy — QuoteDetail's error branch should be able
    // to tell a caller "the API said 404" rather than a bare "something went
    // wrong", the same distinction QuotesList already draws with
    // statusCode() / failureKind().
    api.selectQuote(999);

    httpMock.expectOne('/api/quotes/999').flush(
      { title: 'Quote not found', status: 404, detail: 'No quote with id 999.' },
      { status: 404, statusText: 'Not Found' }
    );

    expect(api.detailState()).toEqual({ status: 'error', statusCode: 404 });
  });

  it('does not let a stale response for a previous selection overwrite the current one', () => {
    // Select quote 1, then — before its response arrives — select quote 2.
    // This is the ordinary "click one row, change your mind, click another"
    // sequence, not a contrived timing.
    api.selectQuote(1);
    const requestForOne = httpMock.expectOne('/api/quotes/1');

    api.selectQuote(2);
    const requestForTwo = httpMock.expectOne('/api/quotes/2');

    // The network answers out of order: quote 2 (the current selection)
    // resolves first, and the now-superseded request for quote 1 resolves
    // after it. This is not a contrived ordering - it is the ordinary case
    // where the first request happened to hit a slower path, a retry, or
    // simply more hops than the second one.
    requestForTwo.flush(quote(2));
    requestForOne.flush(quote(1));

    // Whichever order the network answers in, the screen must show what the
    // user last asked for. An implementation with no cancellation just
    // assigns whatever its subscribe callback last received - quote 1 here -
    // which is the stale-response race this test exists to catch.
    expect(api.detailState()).toEqual({ status: 'ready', quote: quote(2) });
    expect(api.selectedId()).toBe(2);
  });
});
