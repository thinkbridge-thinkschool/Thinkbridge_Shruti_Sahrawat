import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient, httpResource } from '@angular/common/http';
import { catchError, of } from 'rxjs';
import { DEFAULT_SIZE, DetailState, MIN_PAGE, PagedResult, Quote } from './quotes';

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
  private readonly http = inject(HttpClient);

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
    () => `/api/quotes?page=${this.page()}&size=${this.size()}`
  );

  // ---- quote detail: GET /api/quotes/{id} ------------------------------

  /**
   * Which id was last asked for — independent of whether it has loaded yet.
   * QuotesList reads this to highlight the selected row immediately on
   * click, rather than waiting for detailState() to reach 'ready' (which
   * would make the highlight flicker off for the duration of the fetch).
   */
  readonly selectedId = signal<number | null>(null);

  private readonly detailQuote = signal<Quote | null>(null);
  private readonly detailLoading = signal(false);
  private readonly detailFailed = signal(false);

  /**
   * The façade the component reads. Everything above this line is plumbing;
   * QuoteDetail only ever sees a `DetailState`, so how the fetch is actually
   * made — resource, subscription, whatever comes next — can change without
   * touching the component or the tests written against it.
   */
  readonly detailState = computed<DetailState>(() => {
    if (this.detailLoading()) return { status: 'loading' };
    if (this.detailFailed()) return { status: 'error' };
    const quote = this.detailQuote();
    return quote ? { status: 'ready', quote } : { status: 'idle' };
  });

  /** Called from the list when a row is clicked. `null` clears the detail pane. */
  selectQuote(id: number | null): void {
    this.selectedId.set(id);

    if (id === null) {
      this.detailQuote.set(null);
      this.detailFailed.set(false);
      this.detailLoading.set(false);
      return;
    }

    this.detailLoading.set(true);
    this.detailFailed.set(false);

    this.http
      .get<Quote>(`/api/quotes/${id}`)
      .pipe(catchError(() => of(null)))
      .subscribe(quote => {
        this.detailLoading.set(false);
        if (quote) {
          this.detailQuote.set(quote);
        } else {
          this.detailFailed.set(true);
        }
      });
  }
}
