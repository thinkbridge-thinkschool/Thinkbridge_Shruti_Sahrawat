import { Injectable, signal } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { DEFAULT_SIZE, MIN_PAGE, PagedResult, Quote } from './quotes';

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
    () => `/api/quotes?page=${this.page()}&size=${this.size()}`
  );
}
