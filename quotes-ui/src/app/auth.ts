import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthTokenStore, AuthUser } from './auth-header';
import { AppError, MAP_ERRORS } from './error-mapping';

/** Mirrors AuthResponse in QuotesApi/Models/AuthDtos.cs. */
export interface AuthResponse {
  accessToken: string;
  /** Seconds, not an instant — see the DTO's own note on why. */
  expiresIn: number;
  user: AuthUser;
}

/**
 * What a sign-in or registration attempt can come back as.
 *
 * Four outcomes rather than a thrown error, for the same reason
 * QuotesStore.createQuote has three: "your password is wrong", "that email is
 * taken", "this field is malformed" and "the server is down" need four
 * different things on screen, and an exception collapses them into one catch
 * block that can only say something vague.
 */
export type AuthResult =
  | { outcome: 'ok'; user: AuthUser }
  | { outcome: 'invalid'; fieldErrors: Record<string, string[]> }
  | { outcome: 'rejected'; message: string }
  | { outcome: 'failed'; statusCode?: number };

/**
 * Sign in, register, sign out.
 *
 * Holds no session state of its own — AuthTokenStore does, and is what the
 * interceptor and the route guard read. Splitting them that way is what lets
 * the guard run without dragging HttpClient into its injector, and it means
 * the token has exactly one owner rather than a copy here and a copy there
 * that can disagree.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly store = inject(AuthTokenStore);

  /** Who is signed in, or null. Read by the shell to show the account chip. */
  readonly user = this.store.user.asReadonly();

  readonly isSignedIn = this.store.isSignedIn;

  readonly isAdmin = this.store.isAdmin;

  register(email: string, password: string): Promise<AuthResult> {
    return this.submit('/api/auth/register', email, password);
  }

  login(email: string, password: string): Promise<AuthResult> {
    return this.submit('/api/auth/login', email, password);
  }

  /**
   * Ends the session on this device.
   *
   * No server call, because there is nothing on the server to end: a JWT is
   * valid until it expires, and this API keeps no list of live tokens to
   * revoke one against. So "sign out" honestly means "forget the token here" —
   * a copy already taken off this machine keeps working until it expires,
   * which is what the eight-hour lifetime is there to bound. Revocation would
   * need the refresh-token table OrderRefactor's AuthController has.
   */
  signOut(): void {
    this.store.clear();
  }

  private async submit(url: string, email: string, password: string): Promise<AuthResult> {
    try {
      const response = await firstValueFrom(
        this.http.post<AuthResponse>(
          url,
          { email, password },
          { context: new HttpContext().set(MAP_ERRORS, true) },
        ),
      );

      // Persisted only after a successful response. Storing the credentials
      // optimistically — before the server agreed — would leave the app
      // believing it is signed in with a token the API will reject on every
      // subsequent request.
      this.store.persist(response.accessToken, response.user);
      return { outcome: 'ok', user: response.user };
    } catch (error) {
      const appError = error as AppError;

      if (appError.kind === 'validation') {
        return { outcome: 'invalid', fieldErrors: appError.fieldErrors };
      }

      // The wording lives here rather than being read out of the server's
      // ProblemDetails.detail. The server deliberately says as little as
      // possible about why a sign-in failed (see InvalidCredentials in
      // AuthEndpoints.cs); the client is where a human-facing sentence
      // belongs, and reading it off the wire would make the UI's copy
      // depend on a field the API is free to change.
      if (appError.statusCode === 401) {
        return { outcome: 'rejected', message: 'That email and password combination was not recognised.' };
      }

      if (appError.statusCode === 409) {
        return { outcome: 'rejected', message: 'An account with that email already exists. Sign in instead.' };
      }

      return { outcome: 'failed', statusCode: appError.statusCode };
    }
  }
}
