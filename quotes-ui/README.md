# quotes-ui

An Angular 21 front end for the Week-1 Quotes API: a paged, filtered list of
quotes from `GET /api/quotes`, a detail pane reading `GET /api/quotes/{id}`
for whichever row was clicked, and a create form posting to
`POST /api/quotes`.

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
| `src/app/quotes.ts` | The API contract, transcribed from `QuoteDtos.cs`. Types, the `DetailState` union, and the server's own limits. |
| `src/app/quotes-api.ts` | `@Injectable` owning the query state (`page`, `size`), the list `httpResource`, and the quote-detail `httpResource` behind a `detailState` façade. |
| `src/app/quotes-list.ts` | The list screen. Filter state, derived values, row selection, and the template. |
| `src/app/quote-detail.ts` | The detail pane for whichever row is selected. Reads one signal, `detailState()`, and nothing else. |
| `src/app/quotes-api.detail.spec.ts` | The test that caught Day 13 piece 2's two real bugs — see below. |
| `src/app/quote-form.ts` | The create form. Signal Forms, per-field ARIA wiring, focus-first-error on failed submit. |
| `src/app/quote-form.spec.ts` | 15 tests over the form's five states and its a11y contract, including axe. |
| `src/app/request-timeout.ts` | Interceptor bounding how long a request may hang. |
| `src/app/app.config.ts` | Providers. Note what is *absent* — see below. |
| `BRIEF.md` | The prompt Day 13 piece 1 (the list) was built from. |
| `BRIEF-DETAIL.md` | The prompt Day 13 piece 2 (the detail pane) was built from. |
| `BRIEF-FORM.md` | The prompt Day 14 piece 1 (the create form) was built from. |
| `VERIFICATION.md` | Day 13: what was exercised, what came back wrong, what would break. |
| `VERIFICATION-FORM.md` | Day 14: the same, for the form — states, a11y method, four caught bugs. |

---

## Layout and visual design

Design tokens live in `src/styles.css` — colour, both faces, `color-scheme: light
dark`. The previous version set light-mode colours per component and only
*overrode text colour* under `prefers-color-scheme: dark`, never a background:
correct-looking in a light browser, unreadable in a dark one, because nothing
ever painted a dark surface under the now-light text. One `--bg` token, set on
`body`, fixes it globally instead of once per component.

`app.ts` owns the page-level layout (`app.css`'s `.shell` grid) rather than
either child component knowing where it sits: below ~64rem the detail pane
stacks under the list as before, above it the pane becomes a sticky right
column, so selecting a quote doesn't mean scrolling back up past however many
rows are on the page to see what got selected.

Two type faces from Google Fonts, loaded in `index.html`: Source Serif 4 for
the quoted text itself, Inter for everything the UI says about it (labels,
buttons, meta). One thing this cost: Angular's production build fetches and
inlines Google Fonts CSS at build time by default, which hangs indefinitely
with no network reachable to `fonts.googleapis.com` — set
`optimization.fonts.inline: false` in `angular.json` to fetch the stylesheet
at runtime instead. Slightly slower first paint of styled text; a build that
doesn't depend on outbound network access to finish.

Arrow-key navigation between rows (`onListKeydown` in `quotes-list.ts`) moves
*focus* only, not selection — the same division as a native `<select>`: arrows
move you, Enter/Space (free, from using a real `<button>` per row) commits.
Moving focus without also firing `selectQuote()` on every arrow press avoids a
fetch per keystroke while scanning.

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
see [`quotes-api.detail.spec.ts`](src/app/quotes-api.detail.spec.ts)):

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

---

## Tests

One spec, added for piece 2: [`quotes-api.detail.spec.ts`](src/app/quotes-api.detail.spec.ts).
It exists because piece 1's three bugs were all caught by hand — reading the
compiler's error, or clicking through the UI with the API stopped — and piece 2's
two were not: a stale-response race depends on two requests resolving in a
specific order, which is easy to miss by clicking and reliable to force with
`HttpTestingController` controlling exactly when each one flushes.

Six cases against `QuotesApi.selectQuote()` / `detailState()`: idle with nothing
selected, loading, ready, clearing the selection, a 404 carrying its status code,
and the race — select 1, then 2, flush 2's response before 1's, and assert the
screen still shows 2.

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

**These tests do not run in CI.** `.github/workflows/ci.yml` builds and tests
the three .NET projects and gates coverage at 80%; `quotes-ui` is not in it. So
22 green tests are a local check, not a build gate — worth knowing before
treating them as protection against regression.
