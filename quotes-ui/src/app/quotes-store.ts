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

/** Shared empty mask, so an untouched store never allocates a new Set per read. */
const EMPTY_IDS: ReadonlySet<number> = new Set<number>();

/**
 * How long the author search waits after the last keystroke before it asks
 * the server. Matches the shape of retry-backoff.ts's BASE_DELAY_MS: a named
 * constant a test can advance fake timers by by name, rather than a magic
 * number repeated in both places that has to be kept in sync by hand.
 */
export const AUTHOR_FILTER_DEBOUNCE_MS = 300;

/**
 * The store for the quotes-list feature.
 *
 * Signals + a service, no store library. The organising rule, which the old
 * QuotesApi/QuotesList split followed by accident rather than by design:
 *
 *   - **Query state** changes the request. `page`, `size` and
 *     `committedAuthorFilter` are all in the URL, so writing any of them
 *     causes a fetch. `authorFilter` itself is the one exception worth
 *     naming: it is display state for the input box, debounced into
 *     `committedAuthorFilter` before it becomes a request — see the author
 *     search section below for why.
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

  // ---- author search -----------------------------------------------------
  //
  // Two signals, not one, because typing and searching are different
  // events. `authorFilter` is what the input shows and updates on every
  // keystroke; `committedAuthorFilter` is what the server was actually
  // asked for, and only catches up AUTHOR_FILTER_DEBOUNCE_MS after typing
  // pauses. Without the split, a fast typist fires one HTTP request per
  // character - ten requests for "iris", nine of them thrown away before
  // they land.
  //
  // This used to be view state that never left the browser: it narrowed
  // whatever ten rows the current page happened to hold, and a match sitting
  // on page 400 of 1,000 was invisible no matter what was typed. It is query
  // state now, on purpose - the same category as page() and size() - because
  // "search" has to mean the whole collection, not the one page in hand.

  readonly authorFilter = signal('');
  private readonly committedAuthorFilter = signal('');
  private authorFilterTimer: ReturnType<typeof setTimeout> | undefined;

  // ---- server state ----------------------------------------------------

  /**
   * Re-issues whenever page(), size() or committedAuthorFilter() changes,
   * cancelling the in-flight request first. That is why there is no
   * subscribe, no teardown, and no "an older response arrived after a newer
   * one" race to reason about.
   */
  private readonly resource = httpResource<PagedResult<Quote>>(() => {
    const params = new URLSearchParams({
      page: String(this.page()),
      size: String(this.size()),
    });
    const author = this.committedAuthorFilter().trim();
    if (author) params.set('author', author);
    return `/api/quotes?${params.toString()}`;
  });

  // ---- optimistic mutation state ---------------------------------------

  /**
   * Ids hidden on screen ahead of the server confirming their deletion.
   *
   * A **mask over server truth**, not a parallel copy of the list — and a
   * `linkedSignal` rather than a plain `signal` so that it prunes itself.
   * The draft held this as a plain signal that only ever grew, plus a
   * whole-list snapshot to restore on failure, and that was wrong in a way
   * a test caught on the *success* path: once the refetch after a
   * successful delete came back without the deleted row, the id was still
   * sitting in this set, so `totalCount` subtracted it a second time. The
   * server said 1, the mask said "minus one more", the pager read 0 with a
   * row visibly on screen. See VERIFICATION-STATE.md.
   *
   * The rule this encodes: an id is worth masking only while the server is
   * still returning it. Once the server stops, the mask is not just
   * redundant, it is actively wrong. Deriving that from the payload means
   * nothing has to remember to clean up — which is the whole reason the
   * imperative version had a bug and this one structurally cannot.
   */
  private readonly removedIds = linkedSignal<PagedResult<Quote> | undefined, ReadonlySet<number>>({
    source: () => this.resource.value(),
    computation: (payload, previous) => {
      const prev = previous?.value ?? EMPTY_IDS;

      // Mid-fetch: httpResource clears value() to undefined when the request
      // parameters change. Pruning against "no items" would drop every mask
      // and flash the deleted rows back for one frame before the new page
      // lands. Carry the mask forward instead — the same reasoning that
      // makes serverTotal below a linkedSignal rather than a computed.
      if (payload === undefined) return prev;
      if (prev.size === 0) return prev;

      const stillReturned = new Set(payload.items.map((q) => q.id));
      return new Set([...prev].filter((id) => stillReturned.has(id)));
    },
  });

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

  /**
   * The rows to render. The author search now happens server-side (see
   * `resource` above), so this is presentItems() by another name rather
   * than a second, client-side pass over them - kept as its own computed
   * because the template and the tests both already read `visibleQuotes`,
   * and "what's on screen" is a clearer name for a template to depend on
   * than "what survived the optimistic-delete mask".
   */
  readonly visibleQuotes = computed<Quote[]>(() => this.presentItems());

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
    if (this.totalOnPage() > 0) return 'ready';
    // Distinguishing these two needs the *committed* filter, not
    // authorFilter() itself: mid-debounce, authorFilter() can already show
    // the next character typed while this response still describes the
    // previous (or no) search. Reading the committed value keeps the
    // empty-state message in sync with the request that actually produced it.
    return this.committedAuthorFilter().trim() ? 'no-matches' : 'no-data';
  });

  // ---- intents ---------------------------------------------------------

  setAuthorFilter(value: string): void {
    this.authorFilter.set(value);

    // Debounced, not immediate: this now reaches the server (see `resource`
    // above), and firing a request per keystroke would queue up nine
    // requests for "iris" only to throw eight of them away. Resetting to
    // page 1 belongs in the same callback as committing the search term -
    // if it fired on every keystroke instead, a filter typed while on page 4
    // would flash back to page 1 before the user finished typing.
    clearTimeout(this.authorFilterTimer);
    this.authorFilterTimer = setTimeout(() => {
      this.committedAuthorFilter.set(value);
      this.page.set(MIN_PAGE);
    }, AUTHOR_FILTER_DEBOUNCE_MS);
  }

  setSize(value: string | number): void {
    const parsed = Number(value);
    if (!Number.isFinite(parsed)) return;
    this.size.set(Math.min(MAX_SIZE, Math.max(MIN_SIZE, Math.trunc(parsed))));
    this.page.set(MIN_PAGE);
  }

  clearFilter(): void {
    clearTimeout(this.authorFilterTimer);
    this.authorFilter.set('');
    this.committedAuthorFilter.set('');
    this.page.set(MIN_PAGE);
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
    this.removedIds.update((ids) => new Set(ids).add(id));

    try {
      await firstValueFrom(
        this.http.delete<void>(`/api/quotes/${id}`, {
          context: new HttpContext().set(MAP_ERRORS, true),
        }),
      );
      // Refetch so the page, the count and any row promoted from the next
      // page all come from the server rather than being guessed at here.
      // The mask prunes itself when that response lands.
      this.reload();
    } catch (error) {
      const appError = error as AppError;

      if (appError.kind === 'notFound') {
        // Already gone — deleted by someone else, or this is a retry of a
        // request that already succeeded. The user asked for it not to
        // exist, and it does not exist. Rolling back would put a row back
        // that the very next refetch removes again: a flicker that tells
        // the user something untrue. Keep it masked and resync.
        this.reload();
        return;
      }

      // Roll back exactly the row that failed, by lifting its mask — and
      // nothing else. The draft restored a whole-list snapshot here, which
      // cannot distinguish "this delete failed" from "a different delete
      // that happened to overlap succeeded", and would resurrect a row the
      // server had already deleted.
      this.removedIds.update((ids) => {
        const next = new Set(ids);
        next.delete(id);
        return next;
      });

      // 403 gets its own message rather than falling into the generic one
      // below. The template already hides the delete affordance on rows the
      // signed-in user does not own, so this only fires against a stale
      // screen (the row's ownership changed, or it loaded before sign-in) —
      // rare, but "you can only delete your own quotes" tells the user what
      // actually happened instead of implying a transient server hiccup.
      this._deleteError.set(
        appError.kind === 'forbidden'
          ? 'You can only delete your own quotes. It has been put back.'
          : 'Could not delete that quote. It has been put back.',
      );
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
