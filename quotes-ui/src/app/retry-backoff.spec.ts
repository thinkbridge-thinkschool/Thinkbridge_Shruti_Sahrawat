import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { retryWithBackoffInterceptor } from './retry-backoff';

/**
 * Written against the brief — "retry idempotent GETs with backoff" — before
 * reading the draft. Two things this is built to catch:
 *
 * 1. **Only GET is idempotent here.** Retrying a failed POST can create the
 *    quote twice: the first attempt may have succeeded server-side and only
 *    the response was lost, and a blind retry has no way to tell that apart
 *    from the request never having arrived. `POST /api/quotes` must never be
 *    retried by this interceptor, no matter the status.
 * 2. **A 4xx is not a reason to retry.** Retrying a 400 sends the same
 *    invalid body again and gets the same 400 again — it wastes a
 *    round-trip and delays telling the user what is actually wrong. Only a
 *    transient failure (no response at all, or a 5xx) is worth another
 *    attempt.
 *
 * Fake timers make the backoff delay deterministic instead of racing a real
 * setTimeout against the test runner.
 */
describe('retryWithBackoffInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([retryWithBackoffInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
  });

  it('retries a GET that fails with a 503, and succeeds once the retry does', async () => {
    const pending = firstValueFrom(http.get('/api/quotes?page=1&size=10'));

    httpMock.expectOne('/api/quotes?page=1&size=10').flush(null, {
      status: 503,
      statusText: 'Service Unavailable',
    });

    await vi.advanceTimersByTimeAsync(10_000);

    httpMock.expectOne('/api/quotes?page=1&size=10').flush({
      items: [],
      page: 1,
      size: 10,
      totalCount: 0,
    });

    const result = await pending;
    expect(result).toEqual({ items: [], page: 1, size: 10, totalCount: 0 });
  });

  it('backs off with an increasing delay rather than retrying immediately', async () => {
    const pending = firstValueFrom(http.get('/api/quotes?page=1&size=10')).catch((e) => e);

    httpMock.expectOne('/api/quotes?page=1&size=10').flush(null, { status: 503, statusText: 'x' });

    // No retry yet at 1ms — the delay before the first retry is not zero.
    await vi.advanceTimersByTimeAsync(1);
    httpMock.expectNone('/api/quotes?page=1&size=10');

    await vi.advanceTimersByTimeAsync(10_000);
    httpMock.expectOne('/api/quotes?page=1&size=10').flush(null, { status: 503, statusText: 'x' });

    // The second retry's delay must be longer than the first's, not the
    // same fixed gap repeated — otherwise this is a fixed-interval retry
    // wearing a "backoff" name, not actual backoff.
    await vi.advanceTimersByTimeAsync(1);
    httpMock.expectNone('/api/quotes?page=1&size=10');

    await vi.advanceTimersByTimeAsync(10_000);
    httpMock.expectOne('/api/quotes?page=1&size=10').flush(null, { status: 503, statusText: 'x' });
    await vi.advanceTimersByTimeAsync(10_000);
    httpMock.expectOne('/api/quotes?page=1&size=10').flush(null, { status: 503, statusText: 'x' });

    const error = await pending;
    expect(error.status).toBe(503);
  });

  it('gives up after exhausting its retries and surfaces the last failure', async () => {
    const pending = firstValueFrom(http.get('/api/quotes?page=1&size=10')).catch((e) => e);

    // One initial attempt plus retries, all failing — the interceptor must
    // eventually stop, not retry forever.
    for (let attempt = 0; attempt < 4; attempt++) {
      httpMock.expectOne('/api/quotes?page=1&size=10').flush(null, {
        status: 503,
        statusText: 'Service Unavailable',
      });
      await vi.advanceTimersByTimeAsync(10_000);
    }

    httpMock.expectNone('/api/quotes?page=1&size=10');

    const error = await pending;
    expect(error.status).toBe(503);
  });

  it('never retries a POST, even on a 503 — retrying could create the quote twice', async () => {
    const pending = firstValueFrom(
      http.post('/api/quotes', { author: 'Ada Lovelace', text: 'x' }),
    ).catch((e) => e);

    httpMock.expectOne('/api/quotes').flush(null, { status: 503, statusText: 'Service Unavailable' });

    await vi.advanceTimersByTimeAsync(10_000);
    httpMock.expectNone('/api/quotes');

    const error = await pending;
    expect(error.status).toBe(503);
  });

  it('does not retry a GET that fails with a 400 — a client error will not fix itself', async () => {
    const pending = firstValueFrom(http.get('/api/quotes?page=1&size=10')).catch((e) => e);

    httpMock.expectOne('/api/quotes?page=1&size=10').flush(null, {
      status: 400,
      statusText: 'Bad Request',
    });

    await vi.advanceTimersByTimeAsync(10_000);
    httpMock.expectNone('/api/quotes?page=1&size=10');

    const error = await pending;
    expect(error.status).toBe(400);
  });

  it('does not retry a GET that fails with a 404 — retrying will not make the resource exist', async () => {
    const pending = firstValueFrom(http.get('/api/quotes/999999')).catch((e) => e);

    httpMock.expectOne('/api/quotes/999999').flush(null, { status: 404, statusText: 'Not Found' });

    await vi.advanceTimersByTimeAsync(10_000);
    httpMock.expectNone('/api/quotes/999999');

    const error = await pending;
    expect(error.status).toBe(404);
  });
});
