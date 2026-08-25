import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormField, form, maxLength, required, submit } from '@angular/forms/signals';
import { AUTHOR_MAX_LENGTH, Quote, TEXT_MAX_LENGTH } from './quotes';
import { QuotesApi } from './quotes-api';

/**
 * Create-a-quote form, posting to POST /api/quotes.
 *
 * Signal Forms (`@angular/forms/signals`) rather than ReactiveFormsModule:
 * the field state is a signal tree, which is the same model the rest of this
 * app already uses, and it needs no NgModule import.
 */
@Component({
  selector: 'app-quote-form',
  imports: [FormField],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './quote-form.css',
  template: `
    <h2 id="form-heading">Add a quote</h2>

    <form (submit)="onSubmit($event)" aria-labelledby="form-heading">
      <div class="field" [attr.aria-describedby]="authorDescribedBy()">
        <label for="author">Author</label>
        <input
          id="author"
          type="text"
          autocomplete="off"
          [formField]="quoteForm.author"
          [attr.aria-invalid]="showAuthorErrors() ? 'true' : null"
        />
        <p class="hint" id="author-hint">Up to {{ AUTHOR_MAX }} characters.</p>
        @if (showAuthorErrors()) {
          <p class="error" id="author-error">
            @for (e of quoteForm.author().errors(); track e.kind) {
              <span>{{ e.message }}</span>
            }
          </p>
        }
      </div>

      <div class="field" [attr.aria-describedby]="textDescribedBy()">
        <label for="text">Quote</label>
        <textarea
          id="text"
          rows="4"
          [formField]="quoteForm.text"
          [attr.aria-invalid]="showTextErrors() ? 'true' : null"
        ></textarea>
        <p class="hint" id="text-hint">Up to {{ TEXT_MAX }} characters.</p>
        @if (showTextErrors()) {
          <p class="error" id="text-error">
            @for (e of quoteForm.text().errors(); track e.kind) {
              <span>{{ e.message }}</span>
            }
          </p>
        }
      </div>

      <div class="actions">
        <button type="submit" [disabled]="quoteForm().submitting()">
          {{ quoteForm().submitting() ? 'Saving…' : 'Add quote' }}
        </button>
      </div>

      @if (serverFailure(); as failure) {
        <p class="error banner" role="alert">{{ failure }}</p>
      }

      @if (created(); as quote) {
        <p class="success" role="status">
          Added “{{ quote.text }}” — {{ quote.author }}.
          <button type="button" (click)="writeAnother()">Write another</button>
        </p>
      }
    </form>
  `,
})
export class QuoteForm {
  private readonly api = inject(QuotesApi);

  protected readonly AUTHOR_MAX = AUTHOR_MAX_LENGTH;
  protected readonly TEXT_MAX = TEXT_MAX_LENGTH;

  private readonly model = signal({ author: '', text: '' });

  readonly quoteForm = form(this.model, (path) => {
    required(path.author, { message: 'An author is required.' });
    maxLength(path.author, 100, {
      message: 'Author must be 100 characters or fewer.',
    });

    required(path.text, { message: 'Quote text is required.' });
    maxLength(path.text, TEXT_MAX_LENGTH, {
      message: `Quote must be ${TEXT_MAX_LENGTH} characters or fewer.`,
    });
  });

  /** The created quote, held so the success message can name it. */
  readonly created = signal<Quote | null>(null);

  /** A non-field-specific failure: a 500, or nothing answering at all. */
  readonly serverFailure = signal<string | null>(null);

  readonly showAuthorErrors = computed(
    () => this.quoteForm.author().touched() && this.quoteForm.author().errors().length > 0,
  );

  readonly showTextErrors = computed(
    () => this.quoteForm.text().touched() && this.quoteForm.text().errors().length > 0,
  );

  readonly authorDescribedBy = computed(() =>
    this.showAuthorErrors() ? 'author-hint author-error' : 'author-hint',
  );

  readonly textDescribedBy = computed(() =>
    this.showTextErrors() ? 'text-hint text-error' : 'text-hint',
  );

  async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    this.created.set(null);
    this.serverFailure.set(null);

    await submit(this.quoteForm, {
      action: async (f) => {
        const result = await this.api.createQuote(f().value());

        if (result.outcome === 'created') {
          this.created.set(result.quote);
          this.model.set({ author: '', text: '' });
          this.api.reloadList();
          return undefined;
        }

        if (result.outcome === 'failed') {
          this.serverFailure.set(
            result.statusCode === undefined
              ? 'No response from the API.'
              : `The API responded with HTTP ${result.statusCode}.`,
          );
          return undefined;
        }

        // Map the server's per-field messages back onto the fields they
        // belong to, so each one renders next to its own input.
        return [
          ...(result.fieldErrors['author'] ?? []).map((message) => ({
            fieldTree: this.quoteForm.author,
            kind: 'server',
            message,
          })),
          ...(result.fieldErrors['text'] ?? []).map((message) => ({
            fieldTree: this.quoteForm.text,
            kind: 'server',
            message,
          })),
        ];
      },
    });
  }

  writeAnother(): void {
    this.created.set(null);
  }
}
