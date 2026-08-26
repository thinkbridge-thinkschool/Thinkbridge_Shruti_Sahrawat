# The brief — HttpClient + functional interceptors

The prompt given to the agent, before any interceptor existed. Day 15.

---

Two things, in order. First, a characterization test that pins the real
Week-1 API's contract, written and passing before you touch an interceptor
or a component. Then, functional interceptors wired against that same
contract: an auth header, retry-with-backoff on idempotent GETs, and a
typed app error mapped from the API's real 4xx shapes.

## Step 1 — the characterization test

New file, `quotes-ui/src/app/api-contract.spec.ts`. No interceptor, no
component — plain `HttpClient` against `HttpTestingController`, asserting
the shapes below. It has to be green on its own before step 2 starts.

Read the real endpoints and their tests, don't guess the shapes:
`QuotesApi/Extensions/EndpointExtensions.cs` and
`Quotes.Tests.Integration/QuoteEndpointsTests.cs`.

**`GET /api/quotes?page=N&size=N`** — `200`, `PagedResult<QuoteResponse>`:

```json
{ "items": [{ "id": 42, "author": "…", "text": "…", "createdAt": "2026-08-13T09:30:00Z" }], "page": 1, "size": 10, "totalCount": 1 }
```

Envelope fields are `items`/`page`/`size`/`totalCount`, not a bare array and
not `total` — `QuoteEndpointsTests.cs` asserts `page.TotalCount`,
`page.Items`, `page.Page` by exactly those names.

**`POST /api/quotes`, invalid body** — `400`, `ValidationProblemDetails`:

```json
{ "type": "…", "title": "…", "status": 400, "errors": { "Author": ["…"], "Text": ["…"] } }
```

The `errors` keys are capitalised — `Author`, not `author` — same fact the
form already shipped a real bug over on Day 14. Pin it here too so a future
change to either side fails a test instead of surfacing as a blank error
region.

**`GET /api/quotes/{id}`, missing id** — `404`, a *plain* `ProblemDetails`,
not `ValidationProblemDetails`:

```json
{ "type": "…", "title": "Quote not found", "status": 404, "detail": "No quote with id 999999." }
```

No `errors` dictionary at all on this one. If your interceptor or your
client code reads `body.errors` on every 4xx alike instead of branching on
status first, this is the case that gets `undefined` and needs to be
handled as "no field errors," not treated as a parse failure.

## Step 2 — the interceptors

Three functional interceptors (`HttpInterceptorFn`), wired into
`app.config.ts` alongside the existing `requestTimeoutInterceptor`. State
the order you chose and why — interceptors wrap each other, so order changes
behaviour, not just style.

**Auth header.** Attach `Authorization: Bearer <token>` to outgoing
requests when a token is available. Note for the record: check whether the
real Week-1 API's `Program.cs` actually requires this today before assuming
it does — I want to know whether this is exercised against a real 401/403
or is forward-looking plumbing with nothing on the other end yet.

**Retry idempotent GETs with backoff.** Only `GET` — retrying a `POST` risks
creating the quote twice, since a lost *response* to a request the server
already processed looks identical, from here, to a request that never
arrived. Only retry a transient failure: no response at all, or a 5xx. A
4xx means the server was reached and rejected this specific request on
purpose — sending the identical request again gets the identical rejection.
Backoff, not a fixed interval: each retry should wait longer than the last.

**Typed app error from `ProblemDetails`.** Map a failed response to a small
typed error with a message a user could actually be shown, classified
against the two real 4xx shapes above — a validation failure carries the
field errors, a 404 carries whatever `detail` the server sent, a 5xx and a
network failure are distinct from each other and from either 4xx. Before
wiring this everywhere: check what already reads `HttpErrorResponse`
directly in this codebase (`QuotesApi.createQuote`, and `httpResource`'s own
`statusCode()`/`error()` inside `QuotesList` and `QuoteDetail`) and decide
whether rewriting the thrown shape for every request is safe, or whether it
needs to be scoped.

## Reasoning goes in comments

Why retry excludes POST and 4xx. Why the auth header is scoped the way it
is. Why the interceptors are ordered the way they are in `app.config.ts`. I
will be asked to defend each line.

---

## What I changed after reading the output

Written up in [`VERIFICATION-HTTP.md`](VERIFICATION-HTTP.md). The draft's
retry interceptor retried every method and every status — including `POST`
and `4xx` — and its backoff delay callback had the wrong parameter order,
so it "retried" with no actual delay at all. I made the agent fix both.
