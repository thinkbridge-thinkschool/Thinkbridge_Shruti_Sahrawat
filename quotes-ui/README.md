# quotes-ui

An Angular 21 front end for the Week-1 Quotes API. A paged, filtered list of
quotes read from `GET /api/quotes`, with a detail pane below it that reads
`GET /api/quotes/{id}` for whichever row was clicked.

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
| `src/app/quotes-api.detail.spec.ts` | The test that caught piece 2's two real bugs — see below. |
| `src/app/request-timeout.ts` | Interceptor bounding how long a request may hang. |
| `src/app/app.config.ts` | Providers. Note what is *absent* — see below. |
| `BRIEF.md` | The prompt piece 1 (the list) was built from. |
| `BRIEF-DETAIL.md` | The prompt piece 2 (the detail pane) was built from. |
| `VERIFICATION.md` | What was exercised, what came back wrong, what would break. |

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
screen still shows 2. Run with `npm test`.

Piece 1's list screen still has no spec of its own, for the same reason as
before: `ng new`'s generated spec asserted only the scaffold's placeholder text
and was deleted rather than kept, and a spec worth writing there — flushing a
fake page through `provideHttpClientTesting` to assert `visibleQuotes` filters
and that `state()` distinguishes `no-data` from `no-matches` — was not asked for
on Day 13 piece 1. Piece 2 explicitly asked for verification against edge cases
a live run in this environment could not reach, which is what makes it the
exception rather than a change of policy.
