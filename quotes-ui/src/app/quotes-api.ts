import { Injectable, inject, signal } from '@angular/core';
import { HttpClient, HttpContext, httpResource } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { AppError, MAP_ERRORS } from './error-mapping';
import { CreateQuoteRequest, CreateQuoteResult, DEFAULT_SIZE, MIN_PAGE, PagedResult, Quote } from './quotes';

/**
 * Owns the query against GET /api/quotes and the state that parameterises it,
 * plus the POST /api/quotes command.
 *
 * The split is between *what is being asked of the server* and *what the view
 * does with the answer*. Page and size are the query — they change the URL and
 * cause a fetch. The author filter is not: it narrows rows already in hand and
 * never reaches the server, so it stays in the component.
 *
 * httpResource is created in a field initialiser, which is an injection
 * context. A method that creates one lazily is not, unless an Injector is
 * threaded through by hand — a subtle trap, and the reason this is a field
 * rather than a `load()` method.
 *
 * What used to live here too — a second httpResource for GET
 * /api/quotes/{id}, keyed on a `selectedId` signal QuotesList wrote to on
 * row click — moved into QuoteDetail itself as of Day 16's routing. The id
 * is route-owned now: QuoteDetail gets it as a component input bound by
 * `withComponentInputBinding()`, and it is already its own injection context
 * for exactly the reason described above, so there is no longer a reason to
 * route that fetch through here. Nothing outside the detail page ever read
 * `detailState()` except the list's row-highlighting, which routing also
 * removed — see quotes-list.ts.
 */
@Injectable({ providedIn: 'root' })
export class QuotesApi {
  /** Current page. Writable — the component drives it. */
  readonly page = signal(MIN_PAGE);

  /** Rows per page. Clamped by the caller to the server's own 1..100. */
  readonly size = signal(DEFAULT_SIZE);

  /**
   * Re-issues whenever page() or size() changes, cancelling the in-flight
   * request first. That is why there is no subscribe, no teardown, and no
   * "an older response arrived after a newer one" race to reason about.
   */
  readonly result = httpResource<PagedResult<Quote>>(
    () => `/api/quotes?page=${this.page()}&size=${this.size()}`,
  );

  // ---- create: POST /api/quotes ----------------------------------------

  private readonly http = inject(HttpClient);

  /**
   * Posts a new quote and classifies the answer.
   *
   * A promise rather than a resource, because this is a command: it happens
   * once, when the user asks, and it is not derived from any signal the way
   * `result` is. Signal Forms' `submit()` wants something awaitable anyway.
   *
   * Three outcomes rather than a thrown error, for the same reason
   * QuoteDetail's own fetch has a small state union of its own: "the server
   * rejected these fields" and "the server broke" and "nothing answered"
   * need different words on screen, and an exception collapses them into one
   * catch block.
   *
   * The by-hand `HttpErrorResponse` parsing this used to do lives in
   * `errorMappingInterceptor` now — this call opts in with `MAP_ERRORS` and
   * classifies the `AppError` it gets back instead. `GET /api/quotes` and
   * `GET /api/quotes/{id}` deliberately do *not* opt in: `httpResource`'s own
   * `statusCode()`/`error()` already are the typed-enough classification
   * `QuotesList.failureKind` and `QuoteDetail`'s own state are built on, and
   * rewriting the thrown shape under both would mean rewriting both to match
   * — out of scope for what this endpoint needed.
   */
  async createQuote(request: CreateQuoteRequest): Promise<CreateQuoteResult> {
    try {
      const quote = await firstValueFrom(
        this.http.post<Quote>('/api/quotes', request, {
          context: new HttpContext().set(MAP_ERRORS, true),
        }),
      );
      return { outcome: 'created', quote };
    } catch (error) {
      const appError = error as AppError;
      if (appError.kind === 'validation') {
        return { outcome: 'invalid', fieldErrors: appError.fieldErrors };
      }
      return { outcome: 'failed', statusCode: appError.statusCode };
    }
  }

  /** Refetches the current page — called after a successful create. */
  reloadList(): void {
    this.result.reload();
  }
}
