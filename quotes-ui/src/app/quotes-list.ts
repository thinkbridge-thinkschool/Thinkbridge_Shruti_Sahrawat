import { ChangeDetectionStrategy, Component, computed, effect, inject, linkedSignal, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { MAX_SIZE, MIN_PAGE, MIN_SIZE, Quote } from './quotes';
import { QuotesApi } from './quotes-api';

/** The five states this screen can be in. `@switch` renders exactly one. */
type ViewState = 'loading' | 'error' | 'no-data' | 'no-matches' | 'ready';

@Component({
  selector: 'app-quotes-list',
  imports: [DatePipe],
  // Zoneless already means the framework only checks a component when one of
  // its signals changes, so OnPush is close to redundant here. It is set
  // explicitly anyway: it is a compile-time statement that this component has
  // no hidden mutable state, and it keeps the component honest if it is ever
  // dropped into an app that still runs Zone.js.
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './quotes-list.css',
  template: `
    <header>
      <h1>Quotes</h1>
      <p class="sub">
        Reading <code>GET /api/quotes?page&amp;size</code> from the Week&nbsp;1 API.
      </p>
    </header>

    <form class="controls" (submit)="$event.preventDefault()">
      <label>
        Filter by author
        <input
          type="search"
          name="author"
          placeholder="e.g. Ada"
          [value]="authorFilter()"
          (input)="onFilter($any($event.target).value)" />
      </label>

      <label>
        Page size
        <input
          type="number"
          name="size"
          [min]="1"
          [max]="100"
          [value]="size()"
          (input)="onSize($any($event.target).value)" />
      </label>
    </form>

    @switch (state()) {
      @case ('loading') {
        <p class="state" role="status">Loading quotes…</p>
      }

      @case ('error') {
        <div class="state error" role="alert">
          <p>Could not load quotes.</p>
          <!-- statusCode(), not status(). status() is the resource lifecycle
               ('idle' | 'loading' | 'resolved' | 'error' | ...); the HTTP status
               lives on statusCode(), which HttpResourceRef adds on top of the
               base ResourceRef. It is undefined - not 0 - when the request
               never reached a server, which is what a stopped API or a
               misconfigured proxy looks like from inside the browser. Worth
               distinguishing: a 500 means the API answered and failed; nothing
               at all means it was never asked. -->
          <p class="detail">
            @if (failureKind() === 'unreachable') {
              No response from the API. Is it running, and is the dev-server
              proxy pointed at the right port?
            } @else {
              The API responded with HTTP {{ quotes.statusCode() }}.
            }
          </p>
          <button type="button" (click)="quotes.reload()">Try again</button>
        </div>
      }

      @case ('no-data') {
        <p class="state">
          The API returned no quotes on page {{ page() }}.
          @if (page() > 1) {
            <button type="button" (click)="firstPage()">Back to page 1</button>
          }
        </p>
      }

      @case ('no-matches') {
        <p class="state">
          {{ totalOnPage() }} quotes on this page, none by an author matching
          “{{ authorFilter() }}”.
          <button type="button" (click)="clearFilter()">Clear filter</button>
        </p>
      }

      @case ('ready') {
        <ul class="quotes">
          <!-- track q.id, not $index. Tracking the index would make Angular
               reuse the DOM node at position 0 for whatever quote lands there
               next, so paging would mutate existing rows rather than replace
               them — visible as stale text flashing between pages, and as lost
               focus if a row ever holds an input. id is stable and unique. -->
          @for (q of visibleQuotes(); track q.id) {
            <li>
              <blockquote>{{ q.text }}</blockquote>
              <footer>
                <cite>{{ q.author }}</cite>
                <time [attr.datetime]="q.createdAt">
                  {{ q.createdAt | date: 'mediumDate' }}
                </time>
              </footer>
            </li>
          } @empty {
            <!-- Unreachable: 'ready' is only reached when visibleQuotes() is
                 non-empty. Kept because @empty is the block that would catch a
                 future refactor moving the emptiness check out of state(). -->
            <li class="state">Nothing to show.</li>
          }
        </ul>
      }
    }

    <nav class="pager" aria-label="Pagination">
      <button type="button" [disabled]="page() <= 1 || quotes.isLoading()" (click)="prevPage()">
        Previous
      </button>
      <span>
        Page {{ page() }} of {{ totalPages() }}
        <small>({{ totalCount() }} quotes total)</small>
      </span>
      <button
        type="button"
        [disabled]="page() >= totalPages() || quotes.isLoading()"
        (click)="nextPage()">
        Next
      </button>
    </nav>
  `,
})
export class QuotesList {
  // ---- dependencies ---------------------------------------------------

  /**
   * inject(), not a constructor parameter.
   *
   * The practical difference is that a field initialiser can use it. `page`
   * and `size` below are aliases onto signals this service owns, and with
   * constructor injection they could not be initialised until the constructor
   * body ran — which is after every other field initialiser has already
   * referenced them.
   */
  private readonly api = inject(QuotesApi);

  // ---- state ----------------------------------------------------------

  /** Query state, owned by the service because it changes the request URL. */
  readonly page = this.api.page;
  readonly size = this.api.size;

  /**
   * View state, owned here because it never reaches the server — it narrows
   * rows already fetched. Keeping it out of QuotesApi is what stops a
   * keystroke in the filter box from triggering an HTTP request.
   */
  readonly authorFilter = signal('');

  /** The resource itself: value(), isLoading(), error(), statusCode(). */
  readonly quotes = this.api.result;

  // ---- derived state --------------------------------------------------

  /** Derived from two signals: the resource's value, and the filter text. */
  readonly visibleQuotes = computed<Quote[]>(() => {
    const items = this.quotes.value()?.items ?? [];
    const term = this.authorFilter().trim().toLowerCase();
    return term ? items.filter(q => q.author.toLowerCase().includes(term)) : items;
  });

  /**
   * The collection size, held across refetches.
   *
   * This was `computed(() => this.quotes.value()?.totalCount ?? 0)`, which was
   * wrong in a way that only shows up on a slow request. httpResource clears
   * value() to undefined whenever the request parameters change, so during
   * every page change the count collapsed to 0, totalPages collapsed to 1, and
   * the pager read "Page 3 of 1 (0 quotes total)" until the response landed.
   * Normally a flicker; with the API stopped it froze there.
   *
   * linkedSignal exists for exactly this shape: a value derived from a source
   * that should survive the source going momentarily absent. The previous
   * value is carried forward rather than falling back to a zero that is not
   * true. The initial 0 is the only honest answer before anything has loaded.
   */
  readonly totalCount = linkedSignal<number | undefined, number>({
    source: () => this.quotes.value()?.totalCount,
    computation: (incoming, previous) => incoming ?? previous?.value ?? 0,
  });

  /**
   * Deliberately NOT retained. This one is about the page currently rendered,
   * so 0 while loading is the truth — and state() checks isLoading() first, so
   * it never reaches the 'no-data' branch on a transient zero.
   */
  readonly totalOnPage = computed(() => this.quotes.value()?.items.length ?? 0);

  /** Also derived from two signals: totalCount and the page size. */
  readonly totalPages = computed(() =>
    Math.max(1, Math.ceil(this.totalCount() / this.size()))
  );

  /**
   * Whether the request failed at the HTTP layer or never got that far.
   * Different causes, different things for the reader to go and check.
   */
  readonly failureKind = computed<'unreachable' | 'http'>(() =>
    this.quotes.statusCode() === undefined ? 'unreachable' : 'http'
  );

  /**
   * One state, computed once, rendered by a single `@switch`.
   *
   * The two empty cases are kept apart on purpose. "The API has no quotes" and
   * "your filter matched nothing" need different words and different recovery
   * actions, and collapsing them into one `isEmpty` is the kind of shortcut
   * that produces a Clear-filter button on a screen with no filter applied.
   */
  readonly state = computed<ViewState>(() => {
    if (this.quotes.isLoading()) return 'loading';
    if (this.quotes.error()) return 'error';
    if (this.totalOnPage() === 0) return 'no-data';
    if (this.visibleQuotes().length === 0) return 'no-matches';
    return 'ready';
  });

  constructor() {
    // Read-only effect: a running log of state transitions, which is the
    // verification evidence for this screen. It deliberately writes to no
    // signal — an effect that feeds back into the state it observes is how
    // you get a loop that is hard to see and harder to debug.
    effect(() => {
      // eslint-disable-next-line no-console
      console.log('[quotes] state=%s page=%d size=%d shown=%d total=%d',
        this.state(), this.page(), this.size(),
        this.visibleQuotes().length, this.totalCount());
    });
  }

  // ---- intents --------------------------------------------------------

  onFilter(value: string): void {
    this.authorFilter.set(value);
    // Filtering happens client-side over the current page only, so the page
    // number is left alone on purpose. Resetting to page 1 here would imply
    // the filter searches the whole collection, which it does not. See the
    // note in VERIFICATION.md.
  }

  onSize(value: string): void {
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
    this.page.update(p => Math.max(MIN_PAGE, p - 1));
  }

  nextPage(): void {
    this.page.update(p => Math.min(this.totalPages(), p + 1));
  }
}
