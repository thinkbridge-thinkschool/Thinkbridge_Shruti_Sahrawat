import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { LoginPage } from './login-page';
import { AuthTokenStore } from './auth-header';

// Trivial stand-ins for the real destinations. This spec is about what
// LoginPage itself does — set a token, then navigate — not about QuotesList
// or QuoteForm, which have their own specs and their own HTTP traffic that
// has nothing to do with the behaviour under test here.
@Component({ template: '<p>quotes</p>' })
class StubQuotesPage {}

@Component({ template: '<p>new quote</p>' })
class StubNewQuotePage {}

describe('LoginPage', () => {
  let harness: RouterTestingHarness;

  async function settle(): Promise<void> {
    // A router navigation triggered from inside a click handler — rather
    // than through the harness's own navigateByUrl(), which already awaits
    // this internally — chains through more microtask hops than a single
    // httpResource fetch does. Two ticks was enough everywhere else in this
    // codebase; it wasn't here, so this flushes considerably more before
    // giving up, rather than guessing a slightly-larger fixed number.
    for (let i = 0; i < 20; i++) {
      await Promise.resolve();
    }
    TestBed.tick();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'login', component: LoginPage },
          { path: 'quotes', component: StubQuotesPage },
          { path: 'quotes/new', component: StubNewQuotePage },
        ]),
      ],
    });
  });

  it('sets a demo token and goes to /quotes when nothing asked for it', async () => {
    harness = await RouterTestingHarness.create('/login');
    await settle();

    expect(TestBed.inject(AuthTokenStore).token()).toBeNull();

    harness.routeNativeElement!.querySelector<HTMLButtonElement>('button')!.click();
    await settle();

    expect(TestBed.inject(AuthTokenStore).token()).toBe('demo-token');
    expect(TestBed.inject(Router).url).toBe('/quotes');
  });

  it('honours redirectTo — where authGuard actually sends the user back to', async () => {
    harness = await RouterTestingHarness.create('/login?redirectTo=%2Fquotes%2Fnew');
    await settle();

    harness.routeNativeElement!.querySelector<HTMLButtonElement>('button')!.click();
    await settle();

    expect(TestBed.inject(AuthTokenStore).token()).toBe('demo-token');
    expect(TestBed.inject(Router).url).toBe('/quotes/new');
  });
});
