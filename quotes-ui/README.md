# quotes-ui

An Angular 21 front end for the Week-1 Quotes API: a paged, filtered list of
quotes from `GET /api/quotes`, a detail page reading `GET /api/quotes/{id}`
for whichever `:id` is routed to, and a create form posting to
`POST /api/quotes`. Three real routes as of Day 16 — `quotes`, `quotes/new`,
`quotes/:id` — not three panes of one page.

Standalone components, no `NgModule` anywhere, zoneless, and every piece of state
is a signal.

---

## Running it

Two processes. The API first:

```bash
dotnet run --project ../QuotesApi        # note the port it prints
```

Then the UI, with the dev-server proxy pointed at that port:

```bash
npm start -- --proxy-config proxy.conf.json
```

`http://localhost:4200`.

**Check the port.** `proxy.conf.json` targets `http://localhost:5067`. If the
API prints something else, edit it — otherwise every request 404s through the
proxy and the screen shows an error whose status is 404 rather than absent.

**Why a proxy rather than an absolute URL.** The API registers no CORS policy, so
a browser on `:4200` calling `:5067` directly is blocked before the request
leaves the machine. Proxying keeps everything same-origin and requires no change
to the API — the right trade for a front-end exercise.

---

## What is where

| File | What it holds |
|---|---|
| `src/app/quotes.ts` | The API contract, transcribed from `QuoteDtos.cs`. Types and the server's own limits — no `DetailState` here as of Day 16, see `quote-detail.ts`. |
| `src/app/quotes-api.ts` | `@Injectable` owning the query state (`page`, `size`), the list `httpResource`, and the `POST` command. The quote-detail fetch moved off this service on Day 16 — see below. |
| `src/app/quotes-list.ts` | The list screen. Filter state, derived values, a `routerLink` per row, and the template. |
| `src/app/quote-detail.ts` | The detail page, routed at `quotes/:id`. Owns its own `httpResource`, keyed on a route-bound `id` input — no `ActivatedRoute` injected, and no `QuotesApi` involvement at all. |
| `src/app/quote-detail.spec.ts` | Drives `QuoteDetail` through real navigations via `RouterTestingHarness`. Carries forward Day 13 piece 2's two bug-catching cases (swallowed 404, stale-response race) and adds Day 16's id-validation cases. |
| `src/app/quote-id.ts` | Validates a route's `:id` segment against the server's `{id:int}` constraint before it ever reaches a fetch — see `VERIFICATION-ROUTING.md`. |
| `src/app/quote-form.ts` | The create form, routed at `quotes/new` behind `authGuard`. Signal Forms, per-field ARIA wiring, focus-first-error on failed submit. |
| `src/app/quote-form.spec.ts` | 16 tests over the form's five states, its a11y contract, and the rendered hidden-region fix, including axe. |
| `src/app/auth-guard.ts` | `CanActivateFn` guarding `quotes/new`. Returns a `UrlTree` redirect to `/login`, not `false` — see `VERIFICATION-ROUTING.md`. |
| `src/app/login-page.ts` | Where the guard redirects. No real account system — sets a demo token on `AuthTokenStore` and sends the visitor back to wherever they were headed. |
| `src/app/app.routes.ts` | The route table: four lazy `loadComponent` routes plus a wildcard redirect. |
| `src/app/request-timeout.ts` | Interceptor bounding how long a request may hang. |
| `src/app/api-contract.spec.ts` | Day 15's characterization test — pins the real API's shapes before any interceptor existed to consume them. |
| `src/app/auth-header.ts` | Attaches `Authorization: Bearer <token>` to same-origin requests when `AuthTokenStore` has one. |
| `src/app/error-mapping.ts` | Maps a failed response to a typed `AppError`, opt-in per request via the `MAP_ERRORS` context token. |
| `src/app/retry-backoff.ts` | Retries a failed idempotent GET with increasing backoff; two real bugs caught in the same file — see `VERIFICATION-HTTP.md`. |
| `src/app/app.config.ts` | Providers: the ordered interceptor chain, and `provideRouter` with `withComponentInputBinding()` + `withViewTransitions()`. Note what is *absent* — see below. |
| `BRIEF.md` | The prompt Day 13 piece 1 (the list) was built from. |
| `BRIEF-DETAIL.md` | The prompt Day 13 piece 2 (the detail pane) was built from. |
| `BRIEF-FORM.md` | The prompt Day 14 piece 1 (the create form) was built from. |
| `BRIEF-HTTP.md` | The prompt Day 15 (HttpClient + interceptors) was built from. |
| `BRIEF-ROUTING.md` | The prompt Day 16 (routing, lazy loading, guards) was built from. |
| `VERIFICATION.md` | Day 13: what was exercised, what came back wrong, what would break. |
| `VERIFICATION-FORM.md` | Day 14: the same, for the form — states, a11y method, five caught bugs. |
| `SIGNAL-FORMS-VS-REACTIVE.md` | Day 14 piece 2: Signal Forms preview against Reactive Forms — simpler, still rough, and one over-claim checked and rejected rather than assumed. |
| `VERIFICATION-HTTP.md` | Day 15: the characterization test, the interceptors, and two real bugs caught in the same file — one visible in the diff, one only in a fake-timer test. |
| `VERIFICATION-ROUTING.md` | Day 16: lazy-chunk proof from the build output, the guard's redirect round-trip, and an unvalidated route `:id` that reached a real (wrongly-shaped) request. |

---

## Layout and visual design

Design tokens live in `src/styles.css` — colour, both faces, `color-scheme: light
dark`. The previous version set light-mode colours per component and only
*overrode text colour* under `prefers-color-scheme: dark`, never a background:
correct-looking in a light browser, unreadable in a dark one, because nothing
ever painted a dark surface under the now-light text. One `--bg` token, set on
`body`, fixes it globally instead of once per component.

`app.ts` owns the page-level layout — `app.css`'s `.page` wrapper, a single
max-width column every routed page renders inside — rather than any routed
component knowing it needs one. This replaced a two-column `.shell`/`.aside`
grid (list on the left, a sticky detail pane on the right) on Day 16: with
real routing, list and detail are two separate URLs now, not two panes of
one page, so there's nothing left to lay out side by side.

Two type faces from Google Fonts, loaded in `index.html`: Source Serif 4 for
the quoted text itself, Inter for everything the UI says about it (labels,
buttons, meta). One thing this cost: Angular's production build fetches and
inlines Google Fonts CSS at build time by default, which hangs indefinitely
with no network reachable to `fonts.googleapis.com` — set
`optimization.fonts.inline: false` in `angular.json` to fetch the stylesheet
at runtime instead. Slightly slower first paint of styled text; a build that
doesn't depend on outbound network access to finish.

Arrow-key navigation between rows (`onListKeydown` in `quotes-list.ts`) moves
*focus* only, not navigation — the same division as a native `<select>`:
arrows move you, Enter (free, from each row being a real `<a routerLink>`
now) commits. Moving focus without also navigating on every arrow press
avoids a chunk load and a fetch per keystroke while scanning.

---

## Three things worth knowing before reading the code

**Zoneless is the default now.** There is no `provideZonelessChangeDetection()`
in `app.config.ts` and no zone.js in the bundle. Nothing patches `setTimeout` or
`addEventListener`, so Angular is never told "something happened, re-check
everything" — it is told "this signal changed, re-check what read it." The
consequence: a piece of state that is a plain field instead of a signal updates
in memory and never re-renders, silently. That is why everything here is a
signal.

**State is split by whether it changes the request.** `page` and `size` live in
`QuotesApi` because they change the URL and cause a fetch. The author filter
lives in the component because it narrows rows already in hand — keeping it out
of the service is what stops a keystroke triggering an HTTP call.

**The filter only searches the current page.** With 10,000 quotes and a page size
of 100, filtering for an author who exists but is not on the current page shows
"none by an author matching X". Accurate about the page, misleading about the
collection. The API has no author query parameter, so honouring it properly is a
change on the server, not here. The wording says "on this page" so the UI does
not claim more than it knows.

---

## Bugs found by running it

Written up in full in [`VERIFICATION.md`](VERIFICATION.md).

**Piece 1 — the list, three bugs, found by clicking:**

1. **`status()` is not the HTTP status.** On `HttpResourceRef` it is the resource
   lifecycle (`'idle' | 'loading' | 'resolved' | 'error'`); the HTTP status is
   `statusCode()`, and it is `undefined` — not `0` — when the request never
   reached a server. Caught by the compiler.
2. **`?? 0` on a count that was merely late.** `totalCount` collapsed to zero on
   every refetch, so the pager read "Page 3 of 1 (0 quotes total)" mid-fetch.
   Fixed with `linkedSignal`, which carries the previous value forward instead of
   inventing a fact.
3. **A request that never settles renders as "loading" forever.** The proxy
   refused a connection and never answered; the fetch never resolved; the error
   branch never ran. `loading`, `error` and `ready` are not exhaustive — *"never
   answered"* is a fourth state. Fixed with a timeout interceptor.

**Piece 2 — the detail pane, two bugs, found by a test rather than by clicking**
(there is no live Week-1 API in the environment this piece was verified in —
the spec that caught these was `quotes-api.detail.spec.ts` at the time;
Day 16's routing moved the detail fetch onto the routed page itself, and
[`quote-detail.spec.ts`](src/app/quote-detail.spec.ts) is its Day 16
successor, still covering both cases below):

4. **A swallowed error.** The first pass fetched the detail with
   `HttpClient.get().subscribe()` behind a `catchError` that mapped *every*
   failure — a real 404 included — to the same generic "not found." A 404 (the
   quote was deleted after the list loaded) and a dead API are different facts;
   collapsing them lost the distinction `statusCode()` / `failureKind()` already
   draws in the list.
5. **A stale-response race.** The same subscription had no cancellation between
   one selection and the next: click quote 1, then quickly quote 2, and
   whichever response happened to *arrive* last — not whichever was *requested*
   last — was what stayed on screen. Both fixed in the same commit as the test
   that caught them, by replacing the subscription with a second `httpResource`
   keyed on `selectedId`, which cancels the superseded request outright.

**Day 14 — the create form, four bugs, found by a spec run against the draft:**

6. **Server validation errors keyed in the wrong case.** The API returns
   `errors: { "Author": [...] }` — capitalised, unlike every other field it
   serialises — because the keys are C# property names in a dictionary, and
   ASP.NET Core camel-cases property names but not dictionary keys. A client
   reading `errors.author` parses the 400 without error and renders nothing,
   which is indistinguishable from success until the list fails to refresh.
7. **A validator stricter than the API.** Author capped at 100 where
   `[StringLength(200)]` allows 200 — the form refusing input the server
   would have taken, with no way for the user to find out why.
8. **Whitespace passing the client and 400-ing at the server.** Signal Forms'
   `required()` uses `isEmpty()`, which does not trim; `RequiredAttribute`
   does. `"   "` was valid here and invalid there.
9. **The accessible error path not running at all.** `aria-describedby` sat
   on a wrapping `<div>`, where it is announced to nobody, and the form had
   no `novalidate` — so the browser's own bubble pre-empted the submit event
   and the error region, ARIA wiring and focus move never executed.
10. **A fifth bug, found by running it rather than by the suite.** `.error`
    and `.success` both set `display: flex` for their populated state, at
    the same CSS specificity as the browser's own `[hidden] { display: none }`
    rule — and that tie goes to the author stylesheet. So the "hidden"
    banner and success regions rendered as two empty, bordered, coloured
    boxes in the pristine form, passing every test while visibly wrong on
    screen. Fixed with `.error.banner[hidden]` / `.success[hidden]`, both
    explicit `display: none`.

Full write-up, including a bug introduced *while* fixing these, the two
red tests that turned out to be the spec's fault, and the fifth bug that
no test caught until the page was actually opened, in
[`VERIFICATION-FORM.md`](VERIFICATION-FORM.md).

**Day 15 — HttpClient + interceptors, two bugs, both in `retry-backoff.ts`:**

11. **Retried every method and every status.** The draft retried a failed
    `POST` exactly like a failed `GET`, and a `400` exactly like a `503` —
    no condition on either at all. A retried `POST` after a lost response
    can create the quote twice; a retried `4xx` just resends a request the
    server already rejected on purpose. Fixed: only `GET` is retried, and
    only a network failure or a 5xx is treated as worth retrying.
12. **The backoff delay callback had the wrong parameter order.** rxjs's
    `retry({ delay })` calls `(error, retryCount)` — error first. The draft's
    function took one parameter, so it silently received the
    `HttpErrorResponse` as `retryCount`: `BASE_DELAY_MS * 2 ** (error - 1)`
    is `NaN`, and a timer scheduled for `NaN` fires on the next tick. A
    manual check — "did it retry?" — would have said yes; nothing about
    reading the five-line function looked wrong. Caught only by a
    fake-timer assertion that nothing had retried yet after 1ms.

Full write-up, including the characterization test that pinned the real
API's shapes before either interceptor existed, in
[`VERIFICATION-HTTP.md`](VERIFICATION-HTTP.md).

**Day 16 — routing, one bug, a route param nothing validated:**

13. **An unvalidated route `:id` reaching a request.** `QuoteDetail`'s draft
    read the route's `:id` with plain `Number(this.id())` and built the
    fetch URL from it with no check at all — `/quotes/abc` sent
    `GET /api/quotes/NaN`. The real endpoint is declared
    `MapGet("/{id:int}", ...)`, so a non-numeric id doesn't reach the
    handler that returns the friendly, typed 404 this app already knows how
    to render — it falls through to ASP.NET's own generic routing 404
    instead, a different, unhandled shape. Fixed with `parseQuoteId`
    (`quote-id.ts`): reject anything that isn't a plain positive integer
    before a request is ever built, with a dedicated `'invalid'` state for
    what fails that check.

Full write-up, including the lazy-chunk proof from `npm run build`'s own
output and the guard's redirect round-trip, in
[`VERIFICATION-ROUTING.md`](VERIFICATION-ROUTING.md).

---

## Tests

One spec, added for piece 2: `quotes-api.detail.spec.ts` at the time (Day 16's
routing moved the detail fetch off `QuotesApi` and onto the routed page
itself, so this file's Day 16 successor is
[`quote-detail.spec.ts`](src/app/quote-detail.spec.ts) — same cases carried
forward, plus two more; see below).
It exists because piece 1's three bugs were all caught by hand — reading the
compiler's error, or clicking through the UI with the API stopped — and piece 2's
two were not: a stale-response race depends on two requests resolving in a
specific order, which is easy to miss by clicking and reliable to force with
`HttpTestingController` controlling exactly when each one flushes.

Six cases against `QuotesApi.selectQuote()` / `detailState()` at the time: idle
with nothing selected, loading, ready, clearing the selection, a 404 carrying
its status code, and the race — select 1, then 2, flush 2's response before
1's, and assert the screen still shows 2.

[`quote-form.spec.ts`](src/app/quote-form.spec.ts) adds sixteen more for Day 14:
the form's five states (pristine, invalid, submitting, server-error, success),
the contract edges (capitalised error keys, a 200-character author, a
whitespace-only author), the accessibility contract — label association,
`aria-invalid` and `aria-describedby` **on the control** rather than a wrapper,
`novalidate`, focus landing on the first invalid field, and axe-core over the
DOM in both the clean and the error-showing state — and one added after
submission: a `getComputedStyle` check that the hidden banner/success regions
actually render as `display: none`, not just that the `hidden` property is
set. See bug 10 below.

**22 tests, `npm test`.** The form spec is the interesting one to read: it was
written against the brief before the component was reviewed, and the same
fifteen-test version of the file, unchanged, gave 8 failures against the draft
and 21 passes against the fix.

Piece 1's list screen still has no spec of its own, for the same reason as
before: `ng new`'s generated spec asserted only the scaffold's placeholder text
and was deleted rather than kept, and a spec worth writing there — flushing a
fake page through `provideHttpClientTesting` to assert `visibleQuotes` filters
and that `state()` distinguishes `no-data` from `no-matches` — was not asked for
on Day 13 piece 1. That gap is now the oldest untested thing here, and the
honest reason it is still open is scope rather than judgement.

Day 15 adds four more files: [`api-contract.spec.ts`](src/app/api-contract.spec.ts)
(3 cases, the characterization test — green before `auth-header.ts`,
`error-mapping.ts`, or `retry-backoff.ts` existed), [`auth-header.spec.ts`](src/app/auth-header.spec.ts)
(4 cases, including that the token never reaches a cross-origin request),
[`error-mapping.spec.ts`](src/app/error-mapping.spec.ts) (5 cases, one per
`AppError` kind plus the unopted-in pass-through), and
[`retry-backoff.spec.ts`](src/app/retry-backoff.spec.ts) (6 cases, using
Vitest fake timers to make backoff timing exact rather than approximate —
this is the file that caught both of Day 15's bugs).

Day 16 replaces `quotes-api.detail.spec.ts` with
[`quote-detail.spec.ts`](src/app/quote-detail.spec.ts) (still 6 cases —
loading, ready, the 404, the stale-response race carried forward unchanged
in substance, plus two id-validation cases replacing the old file's "idle
with nothing selected" pair, which no longer applies once a routed page
never exists without an `:id`) — driven through `RouterTestingHarness`
navigations rather than a direct service-method call, so the route itself,
not just the component in isolation, is what's under test. It also adds four
new files: [`quote-id.spec.ts`](src/app/quote-id.spec.ts) (9 cases for
`parseQuoteId` in isolation), [`auth-guard.spec.ts`](src/app/auth-guard.spec.ts)
(2 cases: the `UrlTree` shape returned with and without a token),
[`login-page.spec.ts`](src/app/login-page.spec.ts) (2 cases, including the
redirect round-trip back to where the guard caught the visitor), and
[`app.routes.spec.ts`](src/app/app.routes.spec.ts) (5 cases against the real
route table from `app.routes.ts`, not a hand-picked test-only one — the
guard's actual redirect, and an unmatched path actually reaching the list
rather than a blank screen).

**These tests do not run in CI.** `.github/workflows/ci.yml` builds and tests
the three .NET projects and gates coverage at 80%; `quotes-ui` is not in it. So
58 green tests (22 from Days 13–14, 18 from Day 15, 18 net new from Day 16 —
`quote-detail.spec.ts`'s 6 cases replace rather than add to that count) are a
local check, not a build gate — worth knowing before treating them as
protection against regression.
