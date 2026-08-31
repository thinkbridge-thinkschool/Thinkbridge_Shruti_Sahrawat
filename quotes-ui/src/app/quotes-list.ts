import { ChangeDetectionStrategy, Component, effect, inject } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from './auth';
import { QuotesStore } from './quotes-store';

/**
 * The quotes list screen.
 *
 * A thin reader over QuotesStore: template, plus intents that forward
 * straight to the store. It derives nothing of its own — `visibleQuotes`,
 * `totalCount`, `totalPages` and the `listState` machine all used to be
 * computed here, which made it genuinely hard to tell which signals were the
 * source of truth and which were consequences. They are all in the store
 * now, next to the state they are derived from.
 */
@Component({
  selector: 'app-quotes-list',
  imports: [DatePipe, RouterLink],
  // Zoneless already means the framework only checks a component when one of
  // its signals changes, so OnPush is close to redundant here. It is set
  // explicitly anyway: it is a compile-time statement that this component has
  // no hidden mutable state, and it keeps the component honest if it is ever
  // dropped into an app that still runs Zone.js.
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './quotes-list.css',
  template: `
    <header>
      <div class="account">
        @if (auth.user(); as user) {
          <span class="who">
            <svg class="icon" viewBox="0 0 24 24" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
              <circle cx="12" cy="8" r="4"></circle>
              <path d="M4 21c0-4 4-6 8-6s8 2 8 6"></path>
            </svg>
            Signed in as <strong>{{ user.email }}</strong>
            @if (auth.isAdmin()) {
              <span class="role-badge">
                <svg class="icon" viewBox="0 0 24 24" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
                  <path d="M12 2l8 4v6c0 5-3.5 8.5-8 10-4.5-1.5-8-5-8-10V6l8-4z"></path>
                </svg>
                admin
              </span>
            }
          </span>
        }
        <button type="button" class="sign-out" (click)="signOut()">
          <svg class="icon" viewBox="0 0 24 24" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
            <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"></path>
            <polyline points="16 17 21 12 16 7"></polyline>
            <line x1="21" y1="12" x2="9" y2="12"></line>
          </svg>
          Sign out
        </button>
      </div>

      <h1>Quotes</h1>
      <p class="sub">
        @if (auth.isAdmin()) {
          Every quote in the database, including ones created before accounts
          existed. You can delete any of them.
        } @else {
          Every quote in the database. You can delete only the ones you added.
        }
      </p>
    </header>

    <form class="controls" (submit)="$event.preventDefault()">
      <label>
        Filter by author
        <span class="input-icon">
          <svg class="icon" viewBox="0 0 24 24" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
            <circle cx="11" cy="11" r="7"></circle>
            <line x1="21" y1="21" x2="16.65" y2="16.65"></line>
          </svg>
          <input
            type="search"
            name="author"
            placeholder="e.g. Ada"
            [value]="store.authorFilter()"
            (input)="store.setAuthorFilter($any($event.target).value)"
          />
        </span>
      </label>

      <label>
        Page size
        <input
          type="number"
          name="size"
          [min]="1"
          [max]="100"
          [value]="store.size()"
          (input)="store.setSize($any($event.target).value)"
        />
      </label>

      <a class="add-link" routerLink="/quotes/new">
        <svg class="icon" viewBox="0 0 24 24" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
          <line x1="12" y1="5" x2="12" y2="19"></line>
          <line x1="5" y1="12" x2="19" y2="12"></line>
        </svg>
        Add a quote
      </a>
    </form>

    <!-- Present unconditionally with only its content toggling: a live
         region inserted at the same moment it gains text is announced
         unreliably across screen readers, since the assistive tech has to
         be observing the node before the mutation to report it. Same
         reasoning as the form's banner, and the same bug Day 14 shipped
         once by hiding it with a display swap. -->
    <p class="state error delete-error" role="alert" [hidden]="!store.deleteError()">
      {{ store.deleteError() }}
    </p>

    @switch (store.listState()) {
      @case ('loading') {
        <ul class="skeleton-list" role="status">
          <span class="visually-hidden">Loading quotes…</span>
          @for (_ of store.skeletonRows(); track $index) {
            <li class="skeleton-row"></li>
          }
        </ul>
      }

      @case ('error') {
        <div class="state error" role="alert">
          <p>Could not load quotes.</p>
          <!-- statusCode(), not status(). status() is the resource lifecycle
               ('idle' | 'loading' | 'resolved' | 'error' | ...); the HTTP status
               lives on statusCode(). It is undefined - not 0 - when the request
               never reached a server, which is what a stopped API or a
               misconfigured proxy looks like from inside the browser. -->
          <p class="detail">
            @if (store.failureKind() === 'unreachable') {
              No response from the API. Is it running, and is the dev-server proxy pointed at the
              right port?
            } @else if (store.statusCode() === 401) {
              <!-- The token expired, or was signed with a key this server no
                   longer has. Either way the useful thing to offer is a way
                   back in, not a retry button that will fail identically. -->
              Your session has expired. Sign in again to see your quotes.
            } @else {
              The API responded with HTTP {{ store.statusCode() }}.
            }
          </p>
          @if (store.statusCode() === 401) {
            <button type="button" (click)="signOut()">Sign in again</button>
          } @else {
            <button type="button" (click)="store.reload()">Try again</button>
          }
        </div>
      }

      @case ('no-data') {
        <p class="state">
          The API returned no quotes on page {{ store.page() }}.
          @if (store.page() > 1) {
            <button type="button" (click)="store.firstPage()">Back to page 1</button>
          }
        </p>
      }

      @case ('no-matches') {
        <p class="state">
          No quotes found by an author matching “{{ store.authorFilter() }}”.
          <button type="button" (click)="store.clearFilter()">Clear filter</button>
        </p>
      }

      @case ('ready') {
        <ul class="quotes" (keydown)="onListKeydown($event)">
          <!-- track q.id, not $index. Tracking the index would make Angular
               reuse the DOM node at position 0 for whatever quote lands there
               next, so paging would mutate existing rows rather than replace
               them - visible as stale text flashing between pages. -->
          @for (q of store.visibleQuotes(); track q.id) {
            <li>
              <!-- A real <a routerLink>, not a click handler on the <li>, so
                   the row is reachable by keyboard and gets native link
                   focus/activation semantics for free - and so a middle
                   click or "open in new tab" works the way it does on any
                   other link, which a (click) handler cannot offer. -->
              <a class="quote-row" [routerLink]="['/quotes', q.id]">
                <blockquote>{{ q.text }}</blockquote>
                <footer>
                  <cite>{{ q.author }}</cite>
                  <!-- Shown to everyone now, not just an admin: since every
                       user sees every quote (only deleting is restricted),
                       this is what tells an ordinary user why some rows have
                       a delete button and others don't. -->
                  <span
                    class="owner"
                    [attr.title]="q.ownerId === null ? 'Created before login accounts existed' : null"
                    >{{ ownerLabel(q.ownerId) }}</span
                  >
                  <time [attr.datetime]="q.createdAt">
                    {{ q.createdAt | date: 'mediumDate' }}
                  </time>
                </footer>
              </a>
              <!-- Outside the <a>, not inside it: a button nested in a link
                   is invalid HTML and its click would race the navigation.
                   Only rendered for a row this signed-in user is actually
                   allowed to delete - their own, or any row at all for an
                   admin - now that the list shows everyone's quotes to
                   everyone. The server still refuses the request either way
                   (403, see quotes-store.ts), but there is no reason to show
                   an affordance that only leads to a rejection. -->
              @if (auth.isAdmin() || q.ownerId === auth.user()?.id) {
                <button
                  type="button"
                  class="delete"
                  [attr.aria-label]="'Delete quote by ' + q.author"
                  (click)="store.deleteQuote(q.id)"
                >
                  <svg class="icon" viewBox="0 0 24 24" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
                    <polyline points="3 6 5 6 21 6"></polyline>
                    <path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"></path>
                    <path d="M10 11v6"></path>
                    <path d="M14 11v6"></path>
                    <path d="M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"></path>
                  </svg>
                </button>
              }
            </li>
          } @empty {
            <!-- Unreachable: 'ready' is only reached when visibleQuotes() is
                 non-empty. Kept because @empty is the block that would catch
                 a future refactor moving the emptiness check out of the store. -->
            <li class="state">Nothing to show.</li>
          }
        </ul>
      }
    }

    <nav class="pager" aria-label="Pagination">
      <button
        type="button"
        [disabled]="store.page() <= 1 || store.isLoading()"
        (click)="store.prevPage()"
      >
        Previous
      </button>
      <span>
        Page {{ store.page() }} of {{ store.totalPages() }}
        <small>({{ store.totalCount() }} quotes total)</small>
      </span>
      <button
        type="button"
        [disabled]="store.page() >= store.totalPages() || store.isLoading()"
        (click)="store.nextPage()"
      >
        Next
      </button>
    </nav>
  `,
})
export class QuotesList {
  /**
   * inject(), not a constructor parameter — a field initialiser can use it,
   * and the template reads `store` directly rather than re-exposing each
   * signal as a local alias. Aliases were what made the old version look
   * like it owned state it did not.
   */
  protected readonly store = inject(QuotesStore);
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  constructor() {
    // Read-only effect: a running log of state transitions, which is the
    // verification evidence for this screen. It deliberately writes to no
    // signal — an effect that feeds back into the state it observes is how
    // you get a loop that is hard to see and harder to debug.
    effect(() => {
      // eslint-disable-next-line no-console
      console.log(
        '[quotes] state=%s page=%d size=%d shown=%d total=%d',
        this.store.listState(),
        this.store.page(),
        this.store.size(),
        this.store.visibleQuotes().length,
        this.store.totalCount(),
      );
    });
  }

  /**
   * ArrowUp/ArrowDown move keyboard focus between rows without navigating —
   * the same division of labour as a native <select>: arrows move you,
   * Enter (native <a> behaviour, free) commits. Moving focus without also
   * navigating is what stops someone scanning the list with the arrow keys
   * from firing a chunk load and a fetch on every keypress.
   *
   * The only thing left in this component that is genuinely about the DOM
   * rather than about state — which is why it is the only method here that
   * does not simply forward to the store.
   */
  /**
   * Whose quote this is, in words. Shown on every row to every signed-in
   * user now that everyone sees everyone's quotes — it is what explains why
   * some rows carry a delete button and others don't.
   *
   * "No owner" is a real category, not a missing value: those rows were
   * created before the API had accounts, and nobody is allowed to delete
   * them except an admin — backfilling an owner to whoever looked first
   * would invent history the data never had. Named "no owner" rather than
   * the earlier "unowned" because that word alone read, to at least one
   * user, as if something had gone wrong with the quote — the title
   * attribute above spells out why a row can be in this state at all.
   */
  ownerLabel(ownerId: number | null): string {
    if (ownerId === null) return 'no owner';
    return ownerId === this.auth.user()?.id ? 'yours' : `user #${ownerId}`;
  }

  /**
   * Forgets the token and goes to the sign-in page.
   *
   * Navigates rather than leaving the user on a list that is now guaranteed to
   * fail — the guard would bounce them on the next navigation anyway, and
   * doing it here means they never see a 401 they cannot act on.
   */
  async signOut(): Promise<void> {
    this.auth.signOut();
    await this.router.navigateByUrl('/login');
  }

  onListKeydown(event: KeyboardEvent): void {
    if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;

    const rows = Array.from(
      (event.currentTarget as HTMLElement).querySelectorAll<HTMLAnchorElement>('.quote-row'),
    );
    if (rows.length === 0) return;

    event.preventDefault();
    const currentIndex = rows.indexOf(document.activeElement as HTMLAnchorElement);
    const delta = event.key === 'ArrowDown' ? 1 : -1;
    const nextIndex = Math.min(rows.length - 1, Math.max(0, currentIndex + delta));
    rows[nextIndex]?.focus();
  }
}
