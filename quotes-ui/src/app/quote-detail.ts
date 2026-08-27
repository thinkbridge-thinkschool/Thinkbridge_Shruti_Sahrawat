import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { httpResource } from '@angular/common/http';
import { Quote } from './quotes';

/** The states GET /api/quotes/{id} can be in, for whichever :id is routed to. */
type DetailPageState =
  | { status: 'loading' }
  | { status: 'error'; statusCode?: number }
  | { status: 'ready'; quote: Quote };

/**
 * The detail page for one quote, routed at `quotes/:id`.
 *
 * `id` arrives as a component input, bound automatically from the route
 * param by `withComponentInputBinding()` in app.config.ts — no
 * `ActivatedRoute` injected here at all. A field initialiser is still an
 * injection context (same fact `QuotesApi.result` relies on), which is what
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

  private readonly quoteId = computed(() => Number(this.id()));

  private readonly detail = httpResource<Quote>(() => `/api/quotes/${this.quoteId()}`);

  readonly state = computed<DetailPageState>(() => {
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
}
