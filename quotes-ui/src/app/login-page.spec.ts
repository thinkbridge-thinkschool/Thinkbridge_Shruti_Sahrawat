import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { LoginPage } from './login-page';
import { AuthTokenStore } from './auth-header';
import { errorMappingInterceptor } from './error-mapping';

// Trivial stand-ins for the real destinations. This spec is about what
// LoginPage itself does — post credentials, then navigate — not about
// QuotesList or QuoteForm, which have their own specs and their own HTTP
// traffic that has nothing to do with the behaviour under test here.
@Component({ template: '<p>quotes</p>' })
class StubQuotesPage {}

@Component({ template: '<p>new quote</p>' })
class StubNewQuotePage {}

describe('LoginPage', () => {
  let harness: RouterTestingHarness;
  let httpMock: HttpTestingController;

  async function settle(): Promise<void> {
    // A router navigation triggered from inside a submit handler goes through
    // more than plain microtasks: instrumenting this directly (logging
    // router.url from both sides) showed the navigateByUrl() inside
    // LoginPage's action does not resolve until after a real macrotask turn,
    // not merely after more Promise.resolve() hops — apparently something
    // Signal Forms' submit() schedules through. Twenty microtask flushes
    // alone left the router still on /login in the redirectTo() test; adding
    // one macrotask tick resolved it every time. Two microtask ticks was
    // enough everywhere else in this codebase, so the macrotask tick is
    // specific to the submit-then-navigate chain this spec exercises.
    for (let i = 0; i < 20; i++) {
      await Promise.resolve();
    }
    await new Promise((resolve) => setTimeout(resolve, 0));
    TestBed.tick();
  }

  function field(id: string): HTMLInputElement {
    return harness.routeNativeElement!.querySelector<HTMLInputElement>(`#${id}`)!;
  }

  /** Types into a control the way a person does: set the value, fire input. */
  function type(id: string, value: string): void {
    const input = field(id);
    input.value = value;
    input.dispatchEvent(new Event('input', { bubbles: true }));
  }

  async function submitForm(): Promise<void> {
    const form = harness.routeNativeElement!.querySelector('form')!;
    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await settle();
  }

  beforeEach(async () => {
    localStorage.clear();

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([errorMappingInterceptor])),
        provideHttpClientTesting(),
        provideRouter([
          { path: 'login', component: LoginPage },
          { path: 'quotes', component: StubQuotesPage },
          { path: 'quotes/new', component: StubNewQuotePage },
        ]),
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
  });

  it('asks for an email and a password, rather than offering a demo button', async () => {
    harness = await RouterTestingHarness.create('/login');
    await settle();

    // Until Day 19 this page was a single "Continue as a demo user" button,
    // because the API had no accounts to check anything against.
    expect(field('email').type).toBe('email');
    expect(field('password').type).toBe('password');
  });

  it('posts the credentials and goes to /quotes when nothing asked for it', async () => {
    harness = await RouterTestingHarness.create('/login');
    await settle();

    type('email', 'ada@example.com');
    type('password', 'correct-horse');
    await submitForm();

    const req = httpMock.expectOne('/api/auth/login');
    expect(req.request.body).toEqual({ email: 'ada@example.com', password: 'correct-horse' });

    req.flush({
      accessToken: 'jwt-abc',
      expiresIn: 28800,
      user: { id: 4, email: 'ada@example.com', role: 'user' },
    });
    await settle();

    expect(TestBed.inject(AuthTokenStore).token()).toBe('jwt-abc');
    expect(TestBed.inject(Router).url).toBe('/quotes');
  });

  it('honours redirectTo — where authGuard actually sends the user back to', async () => {
    harness = await RouterTestingHarness.create('/login?redirectTo=%2Fquotes%2Fnew');
    await settle();

    type('email', 'ada@example.com');
    type('password', 'correct-horse');
    await submitForm();

    httpMock.expectOne('/api/auth/login').flush({
      accessToken: 'jwt-abc',
      expiresIn: 28800,
      user: { id: 4, email: 'ada@example.com', role: 'user' },
    });
    await settle();

    expect(TestBed.inject(Router).url).toBe('/quotes/new');
  });

  it('shows the rejection and clears only the password when the credentials are wrong', async () => {
    harness = await RouterTestingHarness.create('/login');
    await settle();

    type('email', 'ada@example.com');
    type('password', 'wrong');
    await submitForm();

    httpMock.expectOne('/api/auth/login').flush(
      { title: 'Invalid credentials', status: 401 },
      { status: 401, statusText: 'Unauthorized' },
    );
    await settle();

    const banner = harness.routeNativeElement!.querySelector('.banner')!;
    expect(banner.hasAttribute('hidden')).toBe(false);
    expect(banner.textContent).toContain('not recognised');

    // Retyping an address you just typed correctly is friction with no upside;
    // leaving a rejected password in the box invites submitting it unchanged.
    expect(field('email').value).toBe('ada@example.com');
    expect(field('password').value).toBe('');

    expect(TestBed.inject(Router).url).toBe('/login');
  });

  it('does not call the API at all when the form is empty', async () => {
    harness = await RouterTestingHarness.create('/login');
    await settle();

    await submitForm();

    // submit() skips its action entirely when the form is invalid, so the
    // absence of a request here is the assertion — httpMock.verify() in
    // afterEach would fail if one had been made and left unflushed.
    httpMock.expectNone('/api/auth/login');
  });
});
