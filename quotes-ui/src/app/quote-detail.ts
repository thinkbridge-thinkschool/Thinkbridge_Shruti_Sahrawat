import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { httpResource } from '@angular/common/http';
import { parseQuoteId } from './quote-id';
import { Quote } from './quotes';

/**
 * The states GET /api/quotes/{id} can be in, for whichever :id is routed to.
 *
 * 'invalid' is not an HTTP outcome at all — it is what a :id that never
 * should have reached the API in the first place resolves to instead. See
 * quote-id.ts for why this has to be caught here rather than left for the
 * server's own {id:int}-constrained route to reject.
 */
type DetailPageState =
  | { status: 'invalid'; raw: string }
  | { status: 'loading' }
  | { status: 'error'; statusCode?: number }
  | { status: 'ready'; quote: Quote };

/**
 * The detail page for one quote, routed at `quotes/:id`.
 *
 * `id` arrives as a component input, bound automatically from the route
 * param by `withComponentInputBinding()` in app.config.ts — no
 * `ActivatedRoute` injected here at all. A field initialiser is still an
 * injection context (same fact the store's list resource relies on), which is what
 * lets `httpResource` be created directly on this component instead of
 * needing to live in a service the way `result` still does for the list.
 */
@Component({
  selector: 'app-quote-detail',
  imports: [DatePipe, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './quote-detail.css',
  template: `
    <a class="back" routerLink="/quotes">&larr; Back to quotes</a>

    @switch (state().status) {
      @case ('invalid') {
        <div class="state error" role="alert">
          <p>“{{ raw() }}” isn't a quote id.</p>
          <p class="detail">Quote ids are positive whole numbers — this never reached the API.</p>
        </div>
      }

      @case ('loading') {
        <div class="skeleton-card" role="status">
          <span class="visually-hidden">Loading quote…</span>
        </div>
      }

      @case ('error') {
        <div class="state error" role="alert">
          <p>Could not load this quote.</p>
          @if (statusCode() !== undefined) {
            <p class="detail">The API responded with HTTP {{ statusCode() }}.</p>
          } @else {
            <p class="detail">No response from the API.</p>
          }
        </div>
      }

      @case ('ready') {
        @if (quote(); as q) {
          <article>
            <blockquote>{{ q.text }}</blockquote>
            <footer>
              <cite>{{ q.author }}</cite>
              <time [attr.datetime]="q.createdAt">{{ q.createdAt | date: 'medium' }}</time>
              <span class="id">#{{ q.id }}</span>
            </footer>
          </article>
        }
      }
    }
  `,
})
export class QuoteDetail {
  readonly id = input.required<string>();

  /**
   * `null` for anything that isn't a plain positive integer — see
   * quote-id.ts. The Day 16 draft skipped this step entirely: it computed
   * `Number(this.id())` straight into the fetch URL, so `/quotes/abc` built
   * and sent `/api/quotes/NaN`. The server's own `{id:int}` route
   * constraint would not have matched that request at all — it falls
   * through to ASP.NET's generic routing 404, an empty body with none of
   * the `title`/`detail` fields this page's 'error' branch expects to read.
   * Rejecting it here means that mismatched failure mode is never reached.
   */
  private readonly quoteId = computed(() => parseQuoteId(this.id()));

  /**
   * `undefined` when quoteId() is null — httpResource's own signal for "no
   * request right now" (the same guard the old QuotesApi.detail() used for
   * `selectedId() === null`). An invalid :id issues no request at all.
   */
  private readonly detail = httpResource<Quote>(() => {
    const id = this.quoteId();
    return id === null ? undefined : `/api/quotes/${id}`;
  });

  readonly state = computed<DetailPageState>(() => {
    if (this.quoteId() === null) return { status: 'invalid', raw: this.id() };
    if (this.detail.isLoading()) return { status: 'loading' };
    if (this.detail.error()) return { status: 'error', statusCode: this.detail.statusCode() };
    const quote = this.detail.value();
    return quote ? { status: 'ready', quote } : { status: 'loading' };
  });

  readonly quote = computed(() => {
    const s = this.state();
    return s.status === 'ready' ? s.quote : null;
  });

  readonly statusCode = computed(() => {
    const s = this.state();
    return s.status === 'error' ? s.statusCode : undefined;
  });

  readonly raw = computed(() => {
    const s = this.state();
    return s.status === 'invalid' ? s.raw : '';
  });
}
