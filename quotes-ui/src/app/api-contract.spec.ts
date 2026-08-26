import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { PagedResult, Quote } from './quotes';

/**
 * Characterization test for the real Week-1 API — written and green before
 * any interceptor, component, or piece of UI exists to consume it. Nothing
 * here is guessed: every shape asserted below is read directly from
 * `QuotesApi/Extensions/EndpointExtensions.cs` (the endpoints themselves)
 * and `Quotes.Tests.Integration/QuoteEndpointsTests.cs` (which exercises
 * them against a real SQL Server via Testcontainers in CI). This file pins
 * the same contract against `HttpClient` directly, so a later change to
 * either side — a renamed field, a different error shape — fails a test
 * here rather than surfacing first as a silent blank space in the UI.
 *
 * Three shapes, not one generic "the API returns JSON":
 *
 * 1. `GET /api/quotes?page=N&size=N` — 200, `PagedResult<QuoteResponse>`.
 * 2. `POST /api/quotes` with an invalid body — 400, `ValidationProblemDetails`,
 *    whose `errors` dictionary is keyed by capitalised C# property names
 *    (`"Author"`, not `"author"`) because those keys come from
 *    `ValidationResult.MemberNames` into a `Dictionary`, and ASP.NET Core
 *    camel-cases property *names*, not dictionary *keys*.
 * 3. `GET /api/quotes/{id}` for a missing id — 404, a hand-built plain
 *    `ProblemDetails` with no `errors` dictionary at all — a different 4xx
 *    shape from the 400 above, not a variant of the same one.
 */
describe('Week-1 API contract — characterization', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('GET /api/quotes?page=1&size=10 returns the real PagedResult<Quote> envelope', async () => {
    const pending = firstValueFrom(http.get<PagedResult<Quote>>('/api/quotes?page=1&size=10'));

    httpMock.expectOne('/api/quotes?page=1&size=10').flush({
      items: [
        {
          id: 42,
          author: 'Ada Lovelace',
          text: 'That brain of mine is something more than merely mortal.',
          createdAt: '2026-08-13T09:30:00Z',
        },
      ],
      page: 1,
      size: 10,
      totalCount: 1,
    });

    const result = await pending;

    // The envelope: items/page/size/totalCount, not a bare array and not
    // "total" — QuoteEndpointsTests.cs asserts page.TotalCount, page.Items,
    // page.Page by exactly these names.
    expect(result.page).toBe(1);
    expect(result.size).toBe(10);
    expect(result.totalCount).toBe(1);
    expect(result.items).toHaveLength(1);

    // The item shape: id/author/text/createdAt. createdAt arrives as a
    // string — nothing in the HTTP layer revives ISO-8601 text into a Date.
    const [quote] = result.items;
    expect(quote.id).toBe(42);
    expect(quote.author).toBe('Ada Lovelace');
    expect(typeof quote.createdAt).toBe('string');
  });

  it('POST /api/quotes with an invalid body returns 400 as ValidationProblemDetails with capitalised error keys', async () => {
    const pending = firstValueFrom(
      http.post<never>('/api/quotes', { author: '', text: '' }),
    ).catch((e: HttpErrorResponse) => e);

    httpMock.expectOne('/api/quotes').flush(
      {
        type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: {
          Author: ['The Author field is required.'],
          Text: ['The Text field is required.'],
        },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    const error = await pending;

    expect(error.status).toBe(400);
    // Pinned exactly because it is the one fact this project has already
    // shipped a real bug over (see quote-form.ts / VERIFICATION-FORM.md):
    // capitalised, not the camelCase every other field on this API uses.
    expect(error.error.errors['Author']).toContain('The Author field is required.');
    expect(error.error.errors['author']).toBeUndefined();
  });

  it('GET /api/quotes/{id} for a missing id returns 404 as a plain ProblemDetails, not ValidationProblemDetails', async () => {
    const pending = firstValueFrom(http.get<never>('/api/quotes/999999')).catch(
      (e: HttpErrorResponse) => e,
    );

    httpMock.expectOne('/api/quotes/999999').flush(
      {
        type: 'https://tools.ietf.org/html/rfc9110#section-15.5.5',
        title: 'Quote not found',
        status: 404,
        detail: 'No quote with id 999999.',
      },
      { status: 404, statusText: 'Not Found' },
    );

    const error = await pending;

    expect(error.status).toBe(404);
    expect(error.error.title).toBe('Quote not found');
    expect(error.error.detail).toBe('No quote with id 999999.');
    // The 404 body has no errors dictionary at all — a client that reads
    // `error.error.errors` on every 4xx alike, rather than branching on
    // status first, gets `undefined` here and has to treat that correctly
    // as "no field errors", not as a parse failure.
    expect(error.error.errors).toBeUndefined();
  });
});
