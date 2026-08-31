import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, signal } from '@angular/core';
import { FormField, form, maxLength, required, submit, validate } from '@angular/forms/signals';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthService } from './auth';
import { EMAIL_MAX_LENGTH } from './auth-constants';
import { looksLikeEmail, notOnlyWhitespace } from './auth-validators';

/**
 * Sign in with an email and a password, against POST /api/auth/login.
 *
 * Where `authGuard` sends a navigation it rejected, and where the user comes
 * back from: the guard puts the URL it caught into `redirectTo`, and this page
 * hands them on to it rather than always dumping them on /quotes.
 *
 * Until Day 19 this page was a stub with a single "Continue as a demo user"
 * button, because the API genuinely had no accounts to check a password
 * against. It has now, so the stub is gone.
 */
@Component({
  selector: 'app-login-page',
  imports: [FormField, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './auth-page.css',
  template: `
    <h1 id="login-heading">Sign in</h1>
    <p class="lede">Your quotes are yours — sign in to see them.</p>

    <!--
      novalidate for the same reason as quote-form.ts: without it the browser
      runs its own validation first, shows an unstyleable bubble that screen
      readers announce unreliably, and cancels the submit event — so the error
      region, the aria-describedby wiring and the focus move below never run.
      One accessible error path, not two competing ones.
    -->
    <form novalidate (submit)="onSubmit($event)" aria-labelledby="login-heading">
      <div class="field">
        <label for="email">Email</label>
        <input
          id="email"
          type="email"
          autocomplete="username"
          [formField]="loginForm.email"
          [attr.aria-invalid]="showEmailErrors() ? 'true' : null"
          [attr.aria-describedby]="showEmailErrors() ? 'email-error' : null"
        />
        @if (showEmailErrors()) {
          <p class="error" id="email-error">
            @for (e of loginForm.email().errors(); track e.kind + e.message) {
              <span>{{ e.message }}</span>
            }
          </p>
        }
      </div>

      <div class="field">
        <label for="password">Password</label>
        <!--
          autocomplete="current-password", not "off". Fighting the password
          manager does not make anything safer — it makes people choose
          passwords they can retype, which is the opposite.
        -->
        <input
          id="password"
          type="password"
          autocomplete="current-password"
          [formField]="loginForm.password"
          [attr.aria-invalid]="showPasswordErrors() ? 'true' : null"
          [attr.aria-describedby]="showPasswordErrors() ? 'password-error' : null"
        />
        @if (showPasswordErrors()) {
          <p class="error" id="password-error">
            @for (e of loginForm.password().errors(); track e.kind + e.message) {
              <span>{{ e.message }}</span>
            }
          </p>
        }
      </div>

      <button type="submit" [disabled]="loginForm().submitting()">
        {{ loginForm().submitting() ? 'Signing in…' : 'Sign in' }}
      </button>

      <!--
        In the DOM unconditionally with only its content toggling. A live
        region inserted at the same moment it gains text is announced
        unreliably — the assistive tech has to be observing the node before
        the mutation to report it. Same reasoning as quotes-list.ts.
      -->
      <p class="error banner" role="alert" [hidden]="!failure()">{{ failure() }}</p>
    </form>

    <p class="alt">
      No account yet?
      <a [routerLink]="['/register']" [queryParams]="altQueryParams()">Create one</a>
    </p>
  `,
})
export class LoginPage {
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly host = inject(ElementRef<HTMLElement>);

  private readonly model = signal({ email: '', password: '' });

  readonly loginForm = form(this.model, (path) => {
    required(path.email, { message: 'An email is required.' });
    maxLength(path.email, EMAIL_MAX_LENGTH, {
      message: `Email must be ${EMAIL_MAX_LENGTH} characters or fewer.`,
    });
    validate(path.email, notOnlyWhitespace('An email is required.'));
    validate(path.email, looksLikeEmail('That does not look like an email address.'));

    // Required, and nothing else.
    //
    // The register form checks a minimum length; this one deliberately does
    // not. A length rule here would reject some wrong passwords before asking
    // the server and let others through to a 401, which tells an attacker
    // something about the password policy without them having to guess a
    // single character. It would also lock out anyone whose account predates
    // a future change to that rule.
    required(path.password, { message: 'A password is required.' });
  });

  /** A whole-form failure: wrong credentials, or the API not answering. */
  readonly failure = signal<string | null>(null);

  readonly showEmailErrors = computed(
    () => this.loginForm.email().touched() && this.loginForm.email().errors().length > 0,
  );

  readonly showPasswordErrors = computed(
    () => this.loginForm.password().touched() && this.loginForm.password().errors().length > 0,
  );

  /** Where to go after signing in — what the guard caught, or the list. */
  private redirectTo(): string {
    return this.route.snapshot.queryParamMap.get('redirectTo') || '/quotes';
  }

  /**
   * Carries `redirectTo` across to the register page, so someone who came here
   * from a guarded URL, realised they have no account, and registered instead
   * still lands where they were originally going.
   */
  readonly altQueryParams = computed(() => {
    const redirectTo = this.route.snapshot.queryParamMap.get('redirectTo');
    return redirectTo ? { redirectTo } : {};
  });

  async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    this.failure.set(null);

    await submit(this.loginForm, {
      onInvalid: () => this.focusFirstInvalid(),

      action: async (f) => {
        const { email, password } = f().value();
        const result = await this.auth.login(email, password);

        if (result.outcome === 'ok') {
          await this.router.navigateByUrl(this.redirectTo());
          return undefined;
        }

        if (result.outcome === 'rejected') {
          this.failure.set(result.message);

          // The password is cleared, the email is not. Retyping an address
          // you just typed correctly is friction with no upside; leaving a
          // password that was just rejected in the box invites submitting it
          // again unchanged.
          this.model.update((current) => ({ ...current, password: '' }));
          return undefined;
        }

        if (result.outcome === 'invalid') {
          // The server found something wrong with a field the client thought
          // was fine. Surfaced in the banner rather than dropped: an error
          // nobody can see is worse than one in the wrong place.
          this.failure.set(Object.values(result.fieldErrors).flat().join(' '));
          return undefined;
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
   * Moves focus to the first control the user has to fix.
   *
   * Reads field state rather than rendered attributes: onInvalid runs inside
   * submit(), synchronously after it marks everything touched and before any
   * change detection, so querying '[aria-invalid="true"]' here finds nothing.
   */
  private focusFirstInvalid(): void {
    const inDomOrder = [
      { id: 'email', field: this.loginForm.email },
      { id: 'password', field: this.loginForm.password },
    ];

    const first = inDomOrder.find((entry) => entry.field().invalid());
    if (!first) return;

    (this.host.nativeElement as HTMLElement).querySelector<HTMLElement>(`#${first.id}`)?.focus();
  }
}
