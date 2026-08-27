import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { FormField, form, maxLength, required, submit, validate } from '@angular/forms/signals';
import { RouterLink } from '@angular/router';
import { AUTHOR_MAX_LENGTH, Quote, TEXT_MAX_LENGTH } from './quotes';
import { QuotesApi } from './quotes-api';

/**
 * Create-a-quote form, posting to POST /api/quotes. Routed at `quotes/new`,
 * behind `authGuard` — see auth-guard.ts for why a route the real API
 * doesn't itself require sign-in for is still guarded client-side.
 *
 * Signal Forms (`@angular/forms/signals`) rather than ReactiveFormsModule:
 * the field state is a signal tree, which is the same model the rest of this
 * app already uses, and it needs no NgModule import.
 */
@Component({
  selector: 'app-quote-form',
  imports: [FormField, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './quote-form.css',
  template: `
    <h2 id="form-heading">Add a quote</h2>

    <!--
      novalidate is load-bearing, not boilerplate. Signal Forms writes a
      native "required" attribute onto every control bound with [formField]
      (and "maxlength" where a maxLength validator exists). Without
      novalidate the browser runs its own validation first, shows its own
      bubble, and cancels the submit event — so the error region, the
      aria-describedby wiring and the focus move below never run at all.
      The bubble is also not screen-reader-reliable and cannot be styled.
      One accessible error path, not two competing ones.

      A form[formRoot] directive would supply novalidate for free, but it
      calls submit() with no action, so it cannot do the POST. Hence the
      manual listener plus the explicit attribute.
    -->
    <form novalidate (submit)="onSubmit($event)" aria-labelledby="form-heading">
      <div class="field">
        <label for="author">Author</label>
        <input
          id="author"
          type="text"
          autocomplete="off"
          [formField]="quoteForm.author"
          [attr.aria-invalid]="showAuthorErrors() ? 'true' : null"
          [attr.aria-describedby]="authorDescribedBy()"
        />
        <p class="hint" id="author-hint">Up to {{ AUTHOR_MAX }} characters.</p>
        @if (showAuthorErrors()) {
          <p class="error" id="author-error">
            @for (e of quoteForm.author().errors(); track e.kind + e.message) {
              <span>{{ e.message }}</span>
            }
          </p>
        }
      </div>

      <div class="field">
        <label for="text">Quote</label>
        <textarea
          id="text"
          rows="4"
          [formField]="quoteForm.text"
          [attr.aria-invalid]="showTextErrors() ? 'true' : null"
          [attr.aria-describedby]="textDescribedBy()"
        ></textarea>
        <p class="hint" id="text-hint">Up to {{ TEXT_MAX }} characters.</p>
        @if (showTextErrors()) {
          <p class="error" id="text-error">
            @for (e of quoteForm.text().errors(); track e.kind + e.message) {
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

      <!--
        Both live regions are in the DOM unconditionally, with only their
        *content* toggling. A region inserted at the same moment it gains
        text is announced unreliably across NVDA/JAWS/VoiceOver — the
        assistive tech has to be observing the node before the mutation to
        report it. Empty-but-present is the wiring that actually announces.
      -->
      <p class="error banner" role="alert" [hidden]="!serverFailure()">
        {{ serverFailure() }}
      </p>

      <p class="success" role="status" [hidden]="!created()">
        @if (created(); as quote) {
          Added “{{ quote.text }}” — {{ quote.author }}.
          <button type="button" (click)="writeAnother()">Write another</button>
          <a [routerLink]="['/quotes', quote.id]">View it</a>
        }
      </p>
    </form>
  `,
})
export class QuoteForm {
  private readonly api = inject(QuotesApi);
  private readonly host = inject(ElementRef<HTMLElement>);

  protected readonly AUTHOR_MAX = AUTHOR_MAX_LENGTH;
  protected readonly TEXT_MAX = TEXT_MAX_LENGTH;

  private readonly model = signal({ author: '', text: '' });

  readonly quoteForm = form(this.model, (path) => {
    required(path.author, { message: 'An author is required.' });
    // AUTHOR_MAX_LENGTH, not a rounder-looking number: the server's
    // [StringLength(200)] is the actual limit, and a UI that stops at 100
    // refuses input the API would have accepted, with no way for the user
    // to discover why.
    maxLength(path.author, AUTHOR_MAX_LENGTH, {
      message: `Author must be ${AUTHOR_MAX_LENGTH} characters or fewer.`,
    });
    validate(path.author, notOnlyWhitespace('An author is required.'));

    required(path.text, { message: 'Quote text is required.' });
    maxLength(path.text, TEXT_MAX_LENGTH, {
      message: `Quote must be ${TEXT_MAX_LENGTH} characters or fewer.`,
    });
    validate(path.text, notOnlyWhitespace('Quote text is required.'));
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

  /**
   * aria-describedby goes on the control, and names only ids that exist.
   *
   * Screen readers announce the description of the *focused* control, so
   * this attribute is inert anywhere but the input itself — on a wrapping
   * <div> it is markup that looks wired up and reads as silence. The error
   * id is appended only while the error element is actually rendered,
   * because a reference to a missing id reads as nothing too.
   */
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
      // submit() marks every field touched and skips `action` entirely when
      // the form is invalid, so this is the only branch where a validation
      // failure is observable — and the only place focus can be moved
      // before the user is left staring at a form that did nothing.
      onInvalid: () => this.focusFirstInvalid(),

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

        return this.mapServerFieldErrors(result.fieldErrors);
      },
    });
  }

  /**
   * Maps ValidationProblemDetails.errors onto the fields they belong to.
   *
   * The keys are `Author` and `Text` — capitalised — while every other
   * field this API returns is camelCase. They come from
   * ValidationResult.MemberNames (C# property names) into a Dictionary, and
   * ASP.NET Core's web JSON defaults camel-case property names but leave
   * dictionary keys alone. Verified, not assumed: QuoteEndpointsTests.cs
   * asserts `problem.Errors.Should().ContainKey("Author")` against a real
   * SQL Server.
   *
   * The lookup is case-insensitive anyway. Matching only `Author` would be
   * correct today and would break silently — errors rendering nowhere, the
   * form looking like it succeeded — the day someone sets a
   * DictionaryKeyPolicy on the server. Anything that matches no known field
   * is surfaced in the banner rather than dropped, because an error nobody
   * can see is worse than one in the wrong place.
   */
  private mapServerFieldErrors(fieldErrors: Record<string, string[]>) {
    const fields = {
      author: this.quoteForm.author,
      text: this.quoteForm.text,
    } as const;

    const mapped: {
      fieldTree: (typeof fields)[keyof typeof fields];
      kind: string;
      message: string;
    }[] = [];
    const unattached: string[] = [];

    for (const [key, messages] of Object.entries(fieldErrors)) {
      const field = fields[key.toLowerCase() as keyof typeof fields];
      if (field) {
        mapped.push(...messages.map((message) => ({ fieldTree: field, kind: 'server', message })));
      } else {
        unattached.push(...messages);
      }
    }

    if (unattached.length > 0) {
      this.serverFailure.set(unattached.join(' '));
    }

    return mapped;
  }

  /**
   * Moves focus to the first control the user has to fix.
   *
   * Queried in DOM order rather than from the field tree so that "first"
   * means what the user sees, and it stays right if the fields are ever
   * reordered in the template. A visible error the keyboard never lands on
   * is not an error the user gets to act on — WCAG 3.3.1 wants the failure
   * identified, and identifying it to someone whose focus is still on the
   * submit button at the bottom of the form does not count.
   */
  private focusFirstInvalid(): void {
    // Reads field *state*, not rendered attributes. onInvalid runs inside
    // submit(), synchronously after it marks everything touched and before
    // any change detection — so querying '[aria-invalid="true"]' here finds
    // nothing at all: the signals say invalid, the DOM has not been told
    // yet. Focus went to <body> until this stopped asking the DOM a
    // question only the next render could answer.
    const inDomOrder = [
      { id: 'author', field: this.quoteForm.author },
      { id: 'text', field: this.quoteForm.text },
    ];

    const first = inDomOrder.find((entry) => entry.field().invalid());
    if (!first) return;

    (this.host.nativeElement as HTMLElement).querySelector<HTMLElement>(`#${first.id}`)?.focus();
  }

  writeAnother(): void {
    this.created.set(null);
  }
}

/**
 * Rejects a value that is only whitespace.
 *
 * Needed because the two sides disagree about what "empty" means. The
 * server's [Required] trims before testing, so "   " fails it. Signal
 * Forms' required() uses isEmpty(), which only catches '' / null — so
 * whitespace sails past the client and earns an avoidable 400. Reported
 * with the same message as required(), because from the user's side it is
 * the same mistake.
 */
function notOnlyWhitespace(message: string) {
  return (ctx: { value: () => string }) =>
    ctx.value().length > 0 && ctx.value().trim().length === 0
      ? { kind: 'required', message }
      : null;
}
