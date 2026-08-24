import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
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
 *
 * `settle()` after every state-changing call is not incidental — it is what
 * makes that true. A resource's request-and-response handling runs through
 * a reactive effect and a promise hop, not synchronously inside the call
 * that changed the signal it depends on, so a test has to explicitly let
 * pending microtasks and effects run before asserting on the result. A
 * plain subscription settles synchronously, so the same `settle()` calls
 * are simply redundant against it — which is exactly why one spec file
 * works against both.
 */
describe('QuotesApi — quote detail (GET /api/quotes/{id})', () => {
  let api: QuotesApi;
  let httpMock: HttpTestingController;

  /** Let pending promise hops and reactive effects run to completion. */
  async function settle(): Promise<void> {
    await Promise.resolve();
    await Promise.resolve();
    TestBed.tick();
  }

  async function select(id: number | null): Promise<void> {
    api.selectQuote(id);
    await settle();
  }

  /** Flushes a request unless it was already cancelled — see the race test. */
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
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(QuotesApi);
    httpMock = TestBed.inject(HttpTestingController);
    await settle();

    // QuotesApi also owns the *list* resource (GET /api/quotes?page&size),
    // which fires the moment the service is constructed - nothing to do
    // with the detail endpoint under test here, but a real request that
    // httpMock.verify() would otherwise flag as unaccounted for.
    await respond(
      httpMock.expectOne((req) => req.url.startsWith('/api/quotes?')),
      {
        items: [],
        page: 1,
        size: 10,
        totalCount: 0,
      },
    );
  });

  // ignoreCancelled: the fixed implementation cancels a superseded detail
  // request outright, which is the whole point of the race test below - a
  // cancelled-and-never-flushed request is success, not something to fail on.
  afterEach(() => httpMock.verify({ ignoreCancelled: true }));

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

  it('reports loading the instant a quote is selected, before the response arrives', async () => {
    await select(1);

    expect(api.detailState()).toEqual({ status: 'loading' });
    expect(api.selectedId()).toBe(1);

    await respond(httpMock.expectOne('/api/quotes/1'), quote(1));
  });

  it('reports ready with the fetched quote once the response arrives', async () => {
    await select(1);
    await respond(httpMock.expectOne('/api/quotes/1'), quote(1));

    expect(api.detailState()).toEqual({ status: 'ready', quote: quote(1) });
  });

  it('returns to idle when the selection is cleared', async () => {
    await select(1);
    await respond(httpMock.expectOne('/api/quotes/1'), quote(1));
    expect(api.detailState().status).toBe('ready');

    await select(null);

    expect(api.detailState()).toEqual({ status: 'idle' });
    expect(api.selectedId()).toBeNull();
  });

  it('surfaces a 404 as an error carrying the status code — not as swallowed emptiness', async () => {
    // A quote can legitimately 404: it was deleted (Day 1 supports DELETE
    // /api/quotes/{id}) between the list loading and the row being clicked.
    // That is a real, distinct condition from "nothing selected yet", and
    // from a 500 or a dead proxy — QuoteDetail's error branch should be able
    // to tell a caller "the API said 404" rather than a bare "something went
    // wrong", the same distinction QuotesList already draws with
    // statusCode() / failureKind().
    await select(999);

    await respond(
      httpMock.expectOne('/api/quotes/999'),
      { title: 'Quote not found', status: 404, detail: 'No quote with id 999.' },
      { status: 404, statusText: 'Not Found' },
    );

    expect(api.detailState()).toEqual({ status: 'error', statusCode: 404 });
  });

  it('does not let a stale response for a previous selection overwrite the current one', async () => {
    // Select quote 1, then — before its response arrives — select quote 2.
    // This is the ordinary "click one row, change your mind, click another"
    // sequence, not a contrived timing.
    await select(1);
    const requestForOne = httpMock.expectOne('/api/quotes/1');

    await select(2);
    const requestForTwo = httpMock.expectOne('/api/quotes/2');

    // The network answers out of order: quote 2 (the current selection)
    // resolves first, and the now-superseded request for quote 1 resolves
    // after it — an ordinary case, not a contrived one: the first request
    // hit a slower path, a retry, or simply more hops than the second.
    await respond(requestForTwo, quote(2));
    await respond(requestForOne, quote(1));

    // Whichever order the network answers in, the screen must show what the
    // user last asked for. An implementation with no cancellation just
    // assigns whatever its subscribe callback last received - quote 1 here -
    // which is the stale-response race this test exists to catch.
    expect(api.detailState()).toEqual({ status: 'ready', quote: quote(2) });
    expect(api.selectedId()).toBe(2);
  });
});
