# Verification log — routing, lazy loading, guards

Day 16. What was exercised, what came back wrong, and what breaks if the
route structure or the API's id contract changes.

## How lazy loading was verified

There is no live browser in this environment, so "check the network tab"
becomes: check the one place a lazy chunk is unambiguously either separate
from the initial bundle or it isn't — `npm run build`'s own output. Every
route in `app.routes.ts` uses `loadComponent: () => import(...)`, never a
top-level import, and the build's own chunk graph is the proof, not an
assertion about the source:

```
Initial chunk files | Names         |  Raw size | Estimated transfer size
chunk-GFI65EE3.js   | -             | 272.26 kB |                74.65 kB
main-FCRCJCSD.js    | main          |   1.57 kB |               794 bytes
styles-TKMKTCJE.css | styles        |   1.27 kB |               559 bytes
chunk-SLTAF3YU.js   | -             | 904 bytes |               904 bytes
chunk-WNMFSVOV.js   | -             | 424 bytes |               424 bytes

                    | Initial total | 276.42 kB |                77.33 kB

Lazy chunk files    | Names         |  Raw size | Estimated transfer size
chunk-POK5BTJ3.js   | quote-form    |  53.32 kB |                13.65 kB
chunk-77ELP6HO.js   | quotes-list   |   9.77 kB |                 3.06 kB
chunk-MNNWTX6Z.js   | quote-detail  |   4.54 kB |                 1.64 kB
chunk-DOFCTWJL.js   | login-page    |   1.62 kB |               758 bytes
chunk-R4XZKWP4.js   | -             | 667 bytes |               667 bytes
```

Four named lazy chunks, one per routed component, none of them present in
the initial bundle — `App` itself (`main-FCRCJCSD.js`) is 1.57 kB because it
is now just a `<router-outlet>`. `quote-form` is the largest of the four at
53 kB raw — Signal Forms' own runtime pulls its weight in, which is exactly
the kind of thing that's worth *not* shipping to someone looking at the list.
This is what "the detail route really lazy-loads" means without a browser to
watch it happen in: the bundler had the option to inline it and did not.

## States and edges exercised

| State / edge | How it was forced | Result |
|---|---|---|
| Lazy chunk separation | `npm run build`, inspect the chunk table | four separate named lazy chunks, see above |
| Guard, no token | navigate to `/quotes/new` with `AuthTokenStore.token()` unset | `authGuard` returns a `UrlTree` to `/login?redirectTo=%2Fquotes%2Fnew`; `RouterTestingHarness` confirms the *navigation itself* lands on `LoginPage`, not just that the guard returned something falsy |
| Guard, token set | same navigation, `token.set('demo-token')` first | lands on `QuoteForm` |
| Guard redirect round-trip | `LoginPage`, clicking "Continue as a demo user" with `?redirectTo=%2Fquotes%2Fnew` in the URL | sets the token, then navigates to `/quotes/new` — not hardcoded back to `/quotes` |
| Route param, valid id | navigate to `/quotes/42`, flush `GET /api/quotes/42` | `QuoteDetail.state()` is `{ status: 'ready', quote }`, `id` arrived via `withComponentInputBinding()` with no `ActivatedRoute` injected in the component |
| Route param, 404 | navigate to `/quotes/999999`, flush a 404 `ProblemDetails` | `{ status: 'error', statusCode: 404 }` — the quote can legitimately not exist (deleted, or a stale bookmark), and the page says so, not a blank pane |
| Route param, stale response | navigate `/quotes/1` → `/quotes/2` before quote 1's response arrives, then resolve quote 2 before quote 1 | `state()` shows quote 2 regardless of arrival order — same reused component instance (same matched route, `:id` just changed), same `httpResource` cancellation Day 13 relied on for the old `selectedId`-driven version |
| Route param, missing/invalid id — **the bug** | navigate to `/quotes/abc`, `/quotes/0`, `/quotes/-3` | `{ status: 'invalid', raw: '<value>' }`, **no HTTP request made at all** — see below |
| Unmatched path | navigate to `/` and to `/nonsense/nowhere` | both redirect to `/quotes` via the `**` wildcard, not Angular's default blank screen |
| View Transition | `withViewTransitions()` in `app.config.ts`, all navigation | see "What this can't prove" below — this one genuinely cannot be exercised as a pass/fail assertion in this environment |

**64 tests total, all green:** the 58 carried over from Day 15 (list, detail,
form, and every interceptor — none of their assertions changed, only
`quote-form.spec.ts` gained a router provider so its new `routerLink` can
construct) plus 9 for `parseQuoteId` in isolation, plus what routing itself
added on top of the old detail-spec's six cases — see the table above.

## The bug: an unvalidated route param reaching a request

The Day 16 draft read `:id` with `Number(this.id())`, no check, straight into
the `httpResource` URL. `/quotes/abc` built `/api/quotes/NaN` and sent it.
`quote-detail.spec.ts`'s two id-validation cases were written against the
target behaviour before the fix — `parseQuoteId` didn't exist yet — and both
failed against the draft:

```
FAIL  quote-detail.spec.ts > rejects a non-numeric :id without ever calling the API
  expected { status: 'loading' } to deeply equal { status: 'invalid', raw: 'abc' }

FAIL  quote-detail.spec.ts > rejects a negative or zero :id the same way
  Error: Expected no open requests, found 1: GET /api/quotes/NaN
```

Why this is worse than a cosmetic gap: the real endpoint is declared
`group.MapGet("/{id:int}", ...)` in `EndpointExtensions.cs` — the `:int`
constraint. A request to `/api/quotes/abc` doesn't fail *inside* `GetById`;
it fails to match the route at all, and falls through to ASP.NET's own
generic routing 404 — a body with none of the `title`/`status`/`detail`
fields the real 404 (`GET /api/quotes/999999`, confirmed in
`Quotes.Tests.Integration/QuoteEndpointsTests.cs`) actually has. Two 404s,
two different shapes, and only one of them is the one this app's `'error'`
branch was ever built against. Left alone, a mistyped or hand-edited URL
segment doesn't fail cleanly — it fails in a shape nothing here expected.

Fixed with `parseQuoteId` (`quote-id.ts`): an anchored `^\d+$` regex plus
`Number.isSafeInteger`, not the looser `Number.isInteger(Number(raw))`,
which `' 3 '` and `'3e2'` would both sail through as valid ids that don't
match the literal URL segment they came from. `QuoteDetail` gained an
`'invalid'` state for what fails that check, and its `httpResource`'s URL
function returns `undefined` for it — the same "no request" signal
`QuotesApi`'s old `detail()` used for `selectedId() === null`, applied one
level down now that the fetch lives on the routed component itself.

## What this can't prove

**The View Transition itself.** `withViewTransitions()` calls the browser's
native `document.startViewTransition()` API around each navigation when the
browser supports it, and does nothing (not an error, just a plain
navigation) when it doesn't. Nothing about that is observable through
`HttpTestingController` or a `ComponentFixture` — there is no DOM paint to
inspect, no animation frame to assert on, in this environment. What *is*
verifiable, and was: the router actually reaches the destination component
in every navigation case above, `withViewTransitions()` is present in
`app.config.ts`'s provider list, and it is documented there as a no-op
rather than a break on a browser without support. Confirming the transition
itself paints anything needs a real browser with JavaScript's View
Transitions API — Chromium, opened by a person, watching it happen. Noted
rather than claimed.

**Component ID collisions in test-only stub components.** `login-page.spec.ts`
defines two throwaway stub components (`StubQuotesPage`, `StubNewQuotePage`)
to stand in for real routes without dragging `QuotesApi`'s HTTP traffic into
a test about `LoginPage` alone. Angular logs an `NG0912` warning for them —
both have an empty selector and generate the same internal id — which is
real but harmless for what these two components exist to do (render nothing,
prove where the router landed); a production component always has a real
`selector`, and this warning does not appear anywhere the real app runs.

## What breaks if the route structure or the id contract changes

**The server relaxes `{id:int}` to a looser pattern** (say, allows a UUID or
a slug down the line). `parseQuoteId`'s `^\d+$` would reject a perfectly
valid id the server now accepts, and every existing bookmark/link to that
new id shape would land on `QuoteDetail`'s `'invalid'` state instead of
fetching anything — a change on exactly one side of a contract this app
pins independently, the same category of risk `api-contract.spec.ts`
exists for on the HTTP side. Nothing today catches this automatically; it
would need a characterization test against the real route the way Day 15's
did for the response shapes.

**A real login flow lands.** `authGuard` and `LoginPage` are already correct
plumbing for that — same `AuthTokenStore` Day 15 built, same "redirect with
where to come back to" pattern a real login page would want — but nothing
today ever fails to authenticate; `LoginPage`'s "Continue as a demo user"
button always succeeds. The day a real check can fail, this page needs an
actual failure state, which does not exist yet because there was nothing to
model it against.

**QuotesApi's list resource starts requiring auth too.** `authGuard` only
ever gates *navigation* to `quotes/new` — it says nothing about the requests
QuotesList and QuoteDetail themselves make, which run unauthenticated today
because `GET /api/quotes(/{id})` requires none. If that changes, both routes
would need their own guard or their own 401 handling; today they have
neither, because there is nothing on the real API yet that would ever return
one.
