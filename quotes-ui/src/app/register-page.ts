import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, signal } from '@angular/core';
import { FormField, form, maxLength, required, submit, validate } from '@angular/forms/signals';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthService } from './auth';
import { EMAIL_MAX_LENGTH, PASSWORD_MAX_LENGTH, PASSWORD_MIN_LENGTH } from './auth-constants';
import { atLeast, looksLikeEmail, notOnlyWhitespace } from './auth-validators';

/**
 * Create an account, against POST /api/auth/register.
 *
 * Registration signs you straight in — the endpoint returns the same
 * AuthResponse as login, token included — because a flow that makes someone
 * type the password they just chose into a second form is friction that buys
 * nothing.
 */
@Component({
  selector: 'app-register-page',
  imports: [FormField, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './auth-page.css',
  template: `
    <h1 id="register-heading">Create an account</h1>
    <p class="lede">Quotes you add are visible only to you.</p>

    <form novalidate (submit)="onSubmit($event)" aria-labelledby="register-heading">
      <div class="field">
        <label for="email">Email</label>
        <input
          id="email"
          type="email"
          autocomplete="username"
          [formField]="registerForm.email"
          [attr.aria-invalid]="showEmailErrors() ? 'true' : null"
          [attr.aria-describedby]="showEmailErrors() ? 'email-error' : null"
        />
        @if (showEmailErrors()) {
          <p class="error" id="email-error">
            @for (e of registerForm.email().errors(); track e.kind + e.message) {
              <span>{{ e.message }}</span>
            }
          </p>
        }
      </div>

      <div class="field">
        <label for="password">Password</label>
        <input
          id="password"
          type="password"
          autocomplete="new-password"
          [formField]="registerForm.password"
          [attr.aria-invalid]="showPasswordErrors() ? 'true' : null"
          [attr.aria-describedby]="passwordDescribedBy()"
        />
        <p class="hint" id="password-hint">
          At least {{ MIN }} characters, and at most {{ MAX }}.
        </p>
        @if (showPasswordErrors()) {
          <p class="error" id="password-error">
            @for (e of registerForm.password().errors(); track e.kind + e.message) {
              <span>{{ e.message }}</span>
            }
          </p>
        }
      </div>

      <button type="submit" [disabled]="registerForm().submitting()">
        @if (!registerForm().submitting()) {
          <svg class="icon" viewBox="0 0 24 24" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
            <circle cx="9" cy="8" r="4"></circle>
            <path d="M2 21c0-4 3.5-6.5 7-6.5s7 2.5 7 6.5"></path>
            <line x1="19" y1="8" x2="19" y2="14"></line>
            <line x1="16" y1="11" x2="22" y2="11"></line>
          </svg>
        }
        {{ registerForm().submitting() ? 'Creating account…' : 'Create account' }}
      </button>

      <p class="error banner" role="alert" [hidden]="!failure()">{{ failure() }}</p>
    </form>

    <p class="alt">
      Already have an account?
      <a [routerLink]="['/login']" [queryParams]="altQueryParams()">Sign in</a>
    </p>
  `,
})
export class RegisterPage {
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly host = inject(ElementRef<HTMLElement>);

  protected readonly MIN = PASSWORD_MIN_LENGTH;
  protected readonly MAX = PASSWORD_MAX_LENGTH;

  private readonly model = signal({ email: '', password: '' });

  readonly registerForm = form(this.model, (path) => {
    required(path.email, { message: 'An email is required.' });
    maxLength(path.email, EMAIL_MAX_LENGTH, {
      message: `Email must be ${EMAIL_MAX_LENGTH} characters or fewer.`,
    });
    validate(path.email, notOnlyWhitespace('An email is required.'));
    validate(path.email, looksLikeEmail('That does not look like an email address.'));

    required(path.password, { message: 'A password is required.' });
    validate(
      path.password,
      atLeast(PASSWORD_MIN_LENGTH, `Password must be at least ${PASSWORD_MIN_LENGTH} characters.`),
    );
    // The upper bound is where BCrypt stops reading, not a storage limit -
    // see auth-constants.ts. Enforced here so the user is told, rather than
    // being handed a 400 for a rule no part of the UI mentioned.
    maxLength(path.password, PASSWORD_MAX_LENGTH, {
      message: `Password must be ${PASSWORD_MAX_LENGTH} characters or fewer.`,
    });
  });

  readonly failure = signal<string | null>(null);

  readonly showEmailErrors = computed(
    () => this.registerForm.email().touched() && this.registerForm.email().errors().length > 0,
  );

  readonly showPasswordErrors = computed(
    () => this.registerForm.password().touched() && this.registerForm.password().errors().length > 0,
  );

  /**
   * The hint id is always present; the error id is appended only while the
   * error element actually exists. A describedby pointing at a missing id
   * reads as silence.
   */
  readonly passwordDescribedBy = computed(() =>
    this.showPasswordErrors() ? 'password-hint password-error' : 'password-hint',
  );

  readonly altQueryParams = computed(() => {
    const redirectTo = this.route.snapshot.queryParamMap.get('redirectTo');
    return redirectTo ? { redirectTo } : {};
  });

  private redirectTo(): string {
    return this.route.snapshot.queryParamMap.get('redirectTo') || '/quotes';
  }

  async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    this.failure.set(null);

    await submit(this.registerForm, {
      onInvalid: () => this.focusFirstInvalid(),

      action: async (f) => {
        const { email, password } = f().value();
        const result = await this.auth.register(email, password);

        if (result.outcome === 'ok') {
          await this.router.navigateByUrl(this.redirectTo());
          return undefined;
        }

        if (result.outcome === 'rejected') {
          // "That email is already registered." The password is left alone:
          // it is not what was wrong, and clearing it would make the user
          // retype a correct value to fix a different field.
          this.failure.set(result.message);
          return undefined;
        }

        if (result.outcome === 'invalid') {
          return this.mapServerFieldErrors(result.fieldErrors);
        }

        this.failure.set(
          result.statusCode === undefined
            ? 'No response from the API. Check your connection and try again.'
            : `The API responded with HTTP ${result.statusCode}.`,
        );
        return undefined;
      },
    });
  }

  /**
   * Maps ValidationProblemDetails.errors onto the fields they belong to.
   *
   * The keys are `Email` and `Password` — capitalised, because they come from
   * C# property names into a Dictionary, and ASP.NET Core's web JSON defaults
   * camel-case property names but leave dictionary keys alone. The lookup is
   * case-insensitive anyway: matching only the capitalised form would be
   * correct today and would break silently — errors rendering nowhere, the
   * form looking like it did nothing — the day someone sets a
   * DictionaryKeyPolicy on the server. Same reasoning, and the same shape, as
   * quote-form.ts.
   */
  private mapServerFieldErrors(fieldErrors: Record<string, string[]>) {
    const fields = {
      email: this.registerForm.email,
      password: this.registerForm.password,
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
      this.failure.set(unattached.join(' '));
    }

    return mapped;
  }

  private focusFirstInvalid(): void {
    const inDomOrder = [
      { id: 'email', field: this.registerForm.email },
      { id: 'password', field: this.registerForm.password },
    ];

    const first = inDomOrder.find((entry) => entry.field().invalid());
    if (!first) return;

    (this.host.nativeElement as HTMLElement).querySelector<HTMLElement>(`#${first.id}`)?.focus();
  }
}
