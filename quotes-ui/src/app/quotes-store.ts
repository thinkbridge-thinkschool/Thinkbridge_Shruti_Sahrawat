import { Injectable, computed, inject, linkedSignal, signal } from '@angular/core';
import { HttpClient, HttpContext, httpResource } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { AppError, MAP_ERRORS } from './error-mapping';
import {
  CreateQuoteRequest,
  CreateQuoteResult,
  DEFAULT_SIZE,
  MAX_SIZE,
  MIN_PAGE,
  MIN_SIZE,
  PagedResult,
  Quote,
} from './quotes';

/** The five states the list screen can be in. `@switch` renders exactly one. */
export type ListState = 'loading' | 'error' | 'no-data' | 'no-matches' | 'ready';

/**
 * The store for the quotes-list feature.
 *
 * Signals + a service, no store library. The organising rule, which the old
 * QuotesApi/QuotesList split followed by accident rather than by design:
 *
 *   - **Query state** changes the request. `page` and `size` are in the URL,
 *     so writing them causes a fetch.
 *   - **View state** never reaches the server. `authorFilter` narrows rows
 *     already in hand — it used to live in QuotesList, and moving it here
 *     does not change that: a keystroke still triggers no HTTP call.
 *   - **Server state** is owned by `httpResource`, not copied out of it.
 *   - **Everything else is derived.** `visibleQuotes`, `totalCount`,
 *     `totalPages`, `listState` are all `computed`. If a value can be
 *     calculated from the four above, it is not allowed to be its own
 *     writable signal — that is how two sources of truth start disagreeing.
 *
 * The component reads this and renders. It derives nothing of its own.
 */
@Injectable({ providedIn: 'root' })
export class QuotesStore {
  private readonly http = inject(HttpClient);

  // ---- query state: writing these causes a fetch -----------------------

  readonly page = signal(MIN_PAGE);
  readonly size = signal(DEFAULT_SIZE);

  // ---- view state: never reaches the server ----------------------------

  readonly authorFilter = signal('');

  // ---- server state ----------------------------------------------------

  /**
   * Re-issues whenever page() or size() changes, cancelling the in-flight
   * request first. That is why there is no subscribe, no teardown, and no
   * "an older response arrived after a newer one" race to reason about.
   */
  private readonly resource = httpResource<PagedResult<Quote>>(
    () => `/api/quotes?page=${this.page()}&size=${this.size()}`,
  );

  // ---- optimistic mutation state ---------------------------------------

  /**
   * Rows removed on screen ahead of the server confirming it.
   *
   * A snapshot of the list as it was before this delete started, so a
   * failure can put things back the way they were.
   */
  private readonly rollbackSnapshot = signal<Quote[] | null>(null);

  /** Ids currently hidden because a delete for them is in flight or done. */
  private readonly removedIds = signal<ReadonlySet<number>>(new Set());

  /** The last delete failure, for the component to surface. `null` when fine. */
  private readonly _deleteError = signal<string | null>(null);
  readonly deleteError = this._deleteError.asReadonly();

  // ---- derived ---------------------------------------------------------

  /** What the server last returned for the current page, minus optimistic removals. */
  private readonly serverItems = computed<Quote[]>(() => this.resource.value()?.items ?? []);

  private readonly presentItems = computed<Quote[]>(() => {
    const removed = this.removedIds();
    return this.serverItems().filter((q) => !removed.has(q.id));
  });

  /** Derived from two signals: the rows present, and the filter text. */
  readonly visibleQuotes = computed<Quote[]>(() => {
    const term = this.authorFilter().trim().toLowerCase();
    const items = this.presentItems();
    return term ? items.filter((q) => q.author.toLowerCase().includes(term)) : items;
  });

  /**
   * The collection size, held across refetches and adjusted for optimistic
   * removals.
   *
   * The `linkedSignal` half is a Day 13 fix kept intact: `httpResource`
   * clears `value()` to undefined whenever the request parameters change, so
   * a plain `computed(() => value()?.totalCount ?? 0)` collapsed the count to
   * 0 during every page change — the pager read "Page 3 of 1 (0 quotes
   * total)" until the response landed. linkedSignal carries the previous
   * value forward rather than falling back to a zero that is not true.
   */
  private readonly serverTotal = linkedSignal<number | undefined, number>({
    source: () => this.resource.value()?.totalCount,
    computation: (incoming, previous) => incoming ?? previous?.value ?? 0,
  });

  /**
   * What the pager shows. Subtracting the optimistic removals is what keeps
   * the count honest against the rows actually on screen — the server still
   * thinks there are 42, but the user can see 41.
   */
  readonly totalCount = computed(() => Math.max(0, this.serverTotal() - this.removedIds().size));

  /** About the page currently rendered, so 0 while loading is the truth. */
  private readonly totalOnPage = computed(() => this.presentItems().length);

  readonly totalPages = computed(() => Math.max(1, Math.ceil(this.totalCount() / this.size())));

  /** How many skeleton bars to draw. Capped so size=100 isn't 100 empty rows. */
  readonly skeletonRows = computed(() => Array.from({ length: Math.min(this.size(), 6) }));

  readonly isLoading = this.resource.isLoading;

  /**
   * Whether the request failed at the HTTP layer or never got that far.
   * statusCode() is undefined — not 0 — when nothing answered at all.
   */
  readonly failureKind = computed<'unreachable' | 'http'>(() =>
    this.resource.statusCode() === undefined ? 'unreachable' : 'http',
  );

  readonly statusCode = this.resource.statusCode;

  /**
   * One state, computed once, rendered by a single `@switch`.
   *
   * The two empty cases stay apart on purpose. "The API has no quotes" and
   * "your filter matched nothing" need different words and different
   * recovery actions.
   */
  readonly listState = computed<ListState>(() => {
    if (this.resource.isLoading()) return 'loading';
    if (this.resource.error()) return 'error';
    if (this.totalOnPage() === 0) return 'no-data';
    if (this.visibleQuotes().length === 0) return 'no-matches';
    return 'ready';
  });

  // ---- intents ---------------------------------------------------------

  setAuthorFilter(value: string): void {
    this.authorFilter.set(value);
    // Page deliberately left alone: the filter searches the current page
    // only, and resetting to page 1 would imply it searches the collection.
  }

  setSize(value: string | number): void {
    const parsed = Number(value);
    if (!Number.isFinite(parsed)) return;
    this.size.set(Math.min(MAX_SIZE, Math.max(MIN_SIZE, Math.trunc(parsed))));
    this.page.set(MIN_PAGE);
  }

  clearFilter(): void {
    this.authorFilter.set('');
  }

  firstPage(): void {
    this.page.set(MIN_PAGE);
  }

  prevPage(): void {
    this.page.update((p) => Math.max(MIN_PAGE, p - 1));
  }

  nextPage(): void {
    this.page.update((p) => Math.min(this.totalPages(), p + 1));
  }

  reload(): void {
    this.resource.reload();
  }

  // ---- commands --------------------------------------------------------

  /**
   * Deletes a quote optimistically: the row leaves the list on click, not on
   * the server's answer.
   *
   * DELETE /api/quotes/{id} answers `204 No Content` — no body at all — or a
   * `404` plain ProblemDetails. The 404 is treated as success, not as
   * something to roll back: the server is saying the quote is not there,
   * which is what the user asked for. Restoring the row would put back
   * something that does not exist, and the next refetch would remove it
   * again — a flicker that tells the user nothing true.
   */
  async deleteQuote(id: number): Promise<void> {
    this._deleteError.set(null);

    // Snapshot the list as it stands, so a failure can put it back.
    this.rollbackSnapshot.set(this.presentItems());
    this.removedIds.update((ids) => new Set(ids).add(id));

    try {
      await firstValueFrom(
        this.http.delete<void>(`/api/quotes/${id}`, {
          context: new HttpContext().set(MAP_ERRORS, true),
        }),
      );
      this.reload();
    } catch (error) {
      const appError = error as AppError;

      if (appError.kind === 'notFound') {
        // Already gone. The user's intent is satisfied.
        this.reload();
        return;
      }

      // Put the list back the way it was before this delete started.
      const snapshot = this.rollbackSnapshot();
      if (snapshot) {
        this.removedIds.set(
          new Set(this.serverItems().filter((q) => !snapshot.some((s) => s.id === q.id)).map((q) => q.id)),
        );
      }
      this._deleteError.set('Could not delete that quote. It has been put back.');
    }
  }

  /**
   * Posts a new quote and classifies the answer.
   *
   * A promise rather than a resource, because this is a command: it happens
   * once, when the user asks, and it is not derived from any signal the way
   * the list resource is. Signal Forms' `submit()` wants something awaitable
   * anyway.
   *
   * Three outcomes rather than a thrown error: "the server rejected these
   * fields", "the server broke" and "nothing answered" need different words
   * on screen, and an exception collapses them into one catch block.
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
    this.resource.reload();
  }
}
