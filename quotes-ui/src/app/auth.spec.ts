import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuthService } from './auth';
import { AuthTokenStore } from './auth-header';
import { errorMappingInterceptor } from './error-mapping';

/**
 * AuthService and AuthTokenStore together: the HTTP calls, how each failure
 * shape is classified, and what survives a page reload.
 *
 * errorMappingInterceptor is provided because AuthService opts into it per
 * request (MAP_ERRORS) and reads the AppError it produces. Testing without it
 * would exercise a code path that cannot happen in the real app - every catch
 * block here would receive a raw HttpErrorResponse instead.
 */
describe('AuthService', () => {
  let auth: AuthService;
  let store: AuthTokenStore;
  let httpMock: HttpTestingController;

  const user = { id: 4, email: 'ada@example.com', role: 'user' as const };

  beforeEach(() => {
    // The store restores from localStorage in its constructor, so a value left
    // behind by an earlier test would silently start the next one signed in.
    localStorage.clear();

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([errorMappingInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    auth = TestBed.inject(AuthService);
    store = TestBed.inject(AuthTokenStore);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
  });

  it('starts signed out when nothing is stored', () => {
    expect(auth.isSignedIn()).toBe(false);
    expect(auth.user()).toBeNull();
    expect(auth.isAdmin()).toBe(false);
  });

  it('stores the token and the user after a successful sign-in', async () => {
    const pending = auth.login('ada@example.com', 'correct-horse');

    const req = httpMock.expectOne('/api/auth/login');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ email: 'ada@example.com', password: 'correct-horse' });

    req.flush({ accessToken: 'jwt-abc', expiresIn: 28800, user });

    await expect(pending).resolves.toEqual({ outcome: 'ok', user });
    expect(store.token()).toBe('jwt-abc');
    expect(auth.user()).toEqual(user);
    expect(auth.isSignedIn()).toBe(true);
  });

  it('reports wrong credentials as rejected, and stores nothing', async () => {
    const pending = auth.login('ada@example.com', 'wrong');

    httpMock.expectOne('/api/auth/login').flush(
      { title: 'Invalid credentials', status: 401, detail: 'Not recognised.' },
      { status: 401, statusText: 'Unauthorized' },
    );

    const result = await pending;
    expect(result.outcome).toBe('rejected');

    // The important half: a failed sign-in must not leave the app believing it
    // is signed in with a token the API will reject on every later request.
    expect(store.token()).toBeNull();
    expect(auth.isSignedIn()).toBe(false);
    expect(localStorage.getItem('quotes-ui.session')).toBeNull();
  });

  it('reports an already-registered email as rejected rather than as a field error', async () => {
    const pending = auth.register('ada@example.com', 'correct-horse');

    httpMock.expectOne('/api/auth/register').flush(
      { title: 'Email already registered', status: 409, detail: 'Sign in instead.' },
      { status: 409, statusText: 'Conflict' },
    );

    const result = await pending;
    expect(result).toEqual({
      outcome: 'rejected',
      message: 'An account with that email already exists. Sign in instead.',
    });
  });

  it('passes the server field errors through on a 400, so the form can place them', async () => {
    const pending = auth.register('nope', 'short');

    httpMock.expectOne('/api/auth/register').flush(
      {
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: {
          Email: ['The Email field is not a valid e-mail address.'],
          Password: ['The field Password must be a string with a minimum length of 8.'],
        },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    const result = await pending;
    expect(result.outcome).toBe('invalid');
    if (result.outcome !== 'invalid') return;
    expect(Object.keys(result.fieldErrors)).toEqual(['Email', 'Password']);
  });

  it('classifies a server error separately from a rejection', async () => {
    const pending = auth.login('ada@example.com', 'correct-horse');

    httpMock
      .expectOne('/api/auth/login')
      .flush('boom', { status: 500, statusText: 'Internal Server Error' });

    await expect(pending).resolves.toEqual({ outcome: 'failed', statusCode: 500 });
  });

  it('signs out locally, clearing storage as well as the signals', async () => {
    const pending = auth.login('ada@example.com', 'correct-horse');
    httpMock.expectOne('/api/auth/login').flush({ accessToken: 'jwt-abc', expiresIn: 28800, user });
    await pending;

    auth.signOut();

    expect(store.token()).toBeNull();
    expect(auth.user()).toBeNull();
    expect(localStorage.getItem('quotes-ui.session')).toBeNull();
  });

  it('recognises an admin from the role on the stored user', async () => {
    const admin = { id: 1, email: 'owner@example.com', role: 'admin' as const };
    const pending = auth.login('owner@example.com', 'correct-horse');
    httpMock.expectOne('/api/auth/login').flush({ accessToken: 'jwt-xyz', expiresIn: 28800, user: admin });
    await pending;

    expect(auth.isAdmin()).toBe(true);
  });
});

describe('AuthTokenStore restoration', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => localStorage.clear());

  /** A fresh injector, which is what a page reload amounts to here. */
  function reload(): AuthTokenStore {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({});
    return TestBed.inject(AuthTokenStore);
  }

  it('restores a session written by a previous page load', () => {
    localStorage.setItem(
      'quotes-ui.session',
      JSON.stringify({ token: 'jwt-abc', user: { id: 4, email: 'ada@example.com', role: 'user' } }),
    );

    const store = reload();

    expect(store.token()).toBe('jwt-abc');
    expect(store.user()?.email).toBe('ada@example.com');
  });

  it('treats an unreadable stored session as no session, and clears it', () => {
    // Truncated, half-written, or left over from an older shape of this app.
    // The honest response to "I cannot tell who you are" is a login page, not
    // a crash on startup.
    localStorage.setItem('quotes-ui.session', '{not json');

    const store = reload();

    expect(store.token()).toBeNull();
    expect(localStorage.getItem('quotes-ui.session')).toBeNull();
  });

  it('rejects a stored session missing the user half', () => {
    // A token with nobody attached would leave the app signed in but unable to
    // say as whom - and unable to decide whether to show the admin view.
    localStorage.setItem('quotes-ui.session', JSON.stringify({ token: 'jwt-abc' }));

    const store = reload();

    expect(store.token()).toBeNull();
  });
});
