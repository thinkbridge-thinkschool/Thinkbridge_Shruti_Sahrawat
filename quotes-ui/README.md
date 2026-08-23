# quotes-ui

An Angular 21 front end for the Week-1 Quotes API. One screen: a paged, filtered
list of quotes read from `GET /api/quotes`.

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
| `src/app/quotes.ts` | The API contract, transcribed from `QuoteDtos.cs`. Types and the server's own limits. |
| `src/app/quotes-api.ts` | `@Injectable` owning the query state (`page`, `size`) and the `httpResource`. |
| `src/app/quotes-list.ts` | The screen. Filter state, derived values, and the template. |
| `src/app/request-timeout.ts` | Interceptor bounding how long a request may hang. |
| `src/app/app.config.ts` | Providers. Note what is *absent* — see below. |
| `BRIEF.md` | The prompt this was built from. |
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

## Three bugs found by running it

Written up in full in [`VERIFICATION.md`](VERIFICATION.md).

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

---

## Tests

There are none, deliberately. `ng new` generated a spec asserting the scaffold's
placeholder text, which this app no longer renders; it was deleted rather than
edited to assert something else, because a test that only proves a component can
be constructed proves very little.

A spec worth writing would flush a fake response through
`provideHttpClientTesting` and assert that `visibleQuotes` filters and that
`state()` distinguishes `no-data` from `no-matches`. Not written here because
Day 13 did not ask for it and an unverified test is worse than an absent one.
