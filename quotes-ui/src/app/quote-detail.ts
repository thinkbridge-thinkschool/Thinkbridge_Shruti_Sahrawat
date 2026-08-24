import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { DatePipe } from '@angular/common';
import { QuotesApi } from './quotes-api';

/**
 * The detail pane for whichever quote is currently selected in QuotesList.
 *
 * Deliberately dumb: it reads one signal, `api.detailState()`, and switches
 * on its `status`. It does not know whether that state came from an
 * httpResource, a subscription, or a mock in a test — that is the point of
 * the façade on QuotesApi.
 */
@Component({
  selector: 'app-quote-detail',
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './quote-detail.css',
  template: `
    @switch (state().status) {
      @case ('idle') {
        <p class="state">Select a quote to see its detail.</p>
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
        <!-- @if (...; as q) narrows Quote out of the DetailState union for
             this block, the template equivalent of the status check in
             quote()/statusCode() below - no non-null assertion needed here
             either. -->
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
  private readonly api = inject(QuotesApi);

  readonly state = this.api.detailState;

  /**
   * Narrows the DetailState union down to its Quote payload, or null.
   *
   * The `@switch` above already guarantees `status === 'ready'` when this is
   * read from the template, but a `@switch` case doesn't narrow the type of
   * a signal read elsewhere the way an `if` narrows a local in a .ts file —
   * so the guard is written once here, with `@if (quote(); as q)` doing the
   * rest in the template without a non-null assertion.
   */
  readonly quote = computed(() => {
    const s = this.state();
    return s.status === 'ready' ? s.quote : null;
  });

  readonly statusCode = computed(() => {
    const s = this.state();
    return s.status === 'error' ? s.statusCode : undefined;
  });
}
