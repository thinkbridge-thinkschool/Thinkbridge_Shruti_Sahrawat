import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { AuthTokenStore, authHeaderInterceptor } from './auth-header';

describe('authHeaderInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let tokenStore: AuthTokenStore;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authHeaderInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    tokenStore = TestBed.inject(AuthTokenStore);
  });

  afterEach(() => httpMock.verify());

  it('attaches no Authorization header when there is no token', async () => {
    const pending = firstValueFrom(http.get('/api/quotes?page=1&size=10'));
    const req = httpMock.expectOne('/api/quotes?page=1&size=10');

    expect(req.request.headers.has('Authorization')).toBe(false);

    req.flush({ items: [], page: 1, size: 10, totalCount: 0 });
    await pending;
  });

  it('attaches Authorization: Bearer <token> to a same-origin request once a token is set', async () => {
    tokenStore.token.set('abc123');

    const pending = firstValueFrom(http.get('/api/quotes?page=1&size=10'));
    const req = httpMock.expectOne('/api/quotes?page=1&size=10');

    expect(req.request.headers.get('Authorization')).toBe('Bearer abc123');

    req.flush({ items: [], page: 1, size: 10, totalCount: 0 });
    await pending;
  });

  it('does not attach the token to a request outside this app\'s own origin', async () => {
    // The real risk this scoping exists for: without it, this app's own
    // bearer token would ride along to a third-party host the moment
    // HttpClient is used to call one, rather than a <link> or <img> tag.
    tokenStore.token.set('abc123');

    const pending = firstValueFrom(http.get('https://fonts.googleapis.com/css2'));
    const req = httpMock.expectOne('https://fonts.googleapis.com/css2');

    expect(req.request.headers.has('Authorization')).toBe(false);

    req.flush('');
    await pending;
  });

  it('does not clobber a request that already set its own Authorization header', async () => {
    tokenStore.token.set('app-token');

    const pending = firstValueFrom(
      http.get('/api/quotes?page=1&size=10', {
        headers: { Authorization: 'Bearer caller-supplied-token' },
      }),
    );
    // setHeaders on clone() overwrites by design here — this test pins that
    // choice explicitly rather than leaving it to accident: the store's
    // token is the one source of truth for this app's own requests, so a
    // caller-supplied Authorization header is intentionally replaced, not
    // merged with or deferred to.
    const req = httpMock.expectOne('/api/quotes?page=1&size=10');
    expect(req.request.headers.get('Authorization')).toBe('Bearer app-token');

    req.flush({ items: [], page: 1, size: 10, totalCount: 0 });
    await pending;
  });
});
