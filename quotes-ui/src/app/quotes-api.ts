import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse, httpResource } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import {
  CreateQuoteRequest,
  CreateQuoteResult,
  DEFAULT_SIZE,
  DetailState,
  MIN_PAGE,
  PagedResult,
  Quote,
} from './quotes';

/**
 * Owns the query against GET /api/quotes and the state that parameterises it.
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

  // ---- quote detail: GET /api/quotes/{id} ------------------------------

  /**
   * Which id is currently selected. `null` means nothing is — QuotesList
   * drives this directly on row click, and QuoteDetail's request follows it.
   */
  readonly selectedId = signal<number | null>(null);

  /**
   * Re-issues whenever selectedId() changes, same as `result` above — and
   * for the same reason: the piece 2 draft fetched this by hand with
   * `HttpClient.get().subscribe()`, which has no equivalent cancellation.
   * Selecting quote 1 then quickly quote 2 left both requests in flight, and
   * whichever one's response happened to arrive *last* is the one that won,
   * regardless of which quote was actually still selected. httpResource
   * aborts the superseded request itself; nothing here has to track "is this
   * response for the selection I still care about" by hand.
   *
   * Guarded so that `selectedId() === null` issues no request at all, rather
   * than fetching `/api/quotes/null` — `undefined` is httpResource's signal
   * for "there is nothing to fetch right now."
   */
  private readonly detail = httpResource<Quote>(() => {
    const id = this.selectedId();
    return id === null ? undefined : `/api/quotes/${id}`;
  });

  /**
   * The façade the component reads. Everything above this line is plumbing;
   * QuoteDetail only ever sees a `DetailState`, so how the fetch is actually
   * made — resource, subscription, whatever comes next — can change without
   * touching the component or the tests written against it.
   *
   * statusCode() flows through untouched on error, on purpose: it is what
   * lets the draft's bug show up in a test at all. A `catchError` that maps
   * every failure to the same `{ status: 'error' }` is indistinguishable
   * from this one for a 500, and that is exactly the problem — a real 404
   * (the quote was deleted after the list loaded) and an unreachable API
   * need to stay tellable apart, the same distinction `failureKind` draws in
   * QuotesList.
   */
  readonly detailState = computed<DetailState>(() => {
    if (this.selectedId() === null) return { status: 'idle' };
    if (this.detail.isLoading()) return { status: 'loading' };
    if (this.detail.error()) return { status: 'error', statusCode: this.detail.statusCode() };
    const quote = this.detail.value();
    return quote ? { status: 'ready', quote } : { status: 'idle' };
  });

  /** Called from the list when a row is clicked. `null` clears the detail pane. */
  selectQuote(id: number | null): void {
    this.selectedId.set(id);
  }

  // ---- create: POST /api/quotes ----------------------------------------

  private readonly http = inject(HttpClient);

  /**
   * Posts a new quote and classifies the answer.
   *
   * A promise rather than a resource, because this is a command: it happens
   * once, when the user asks, and it is not derived from any signal the way
   * `result` and `detail` are. Signal Forms' `submit()` wants something
   * awaitable anyway.
   *
   * Three outcomes rather than a thrown error, for the same reason the
   * detail path has a DetailState: "the server rejected these fields" and
   * "the server broke" and "nothing answered" need different words on
   * screen, and an exception collapses them into one catch block.
   */
  async createQuote(request: CreateQuoteRequest): Promise<CreateQuoteResult> {
    try {
      const quote = await firstValueFrom(this.http.post<Quote>('/api/quotes', request));
      return { outcome: 'created', quote };
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 400) {
        const problem = error.error as { errors?: Record<string, string[]> } | null;
        return { outcome: 'invalid', fieldErrors: problem?.errors ?? {} };
      }
      const statusCode = error instanceof HttpErrorResponse ? error.status : undefined;
      return { outcome: 'failed', statusCode };
    }
  }

  /** Refetches the current page — called after a successful create. */
  reloadList(): void {
    this.result.reload();
  }
}
