[← Back to full README](../../README.md)

## Day 15 — HttpClient + functional interceptors

[`quotes-ui/BRIEF-HTTP.md`](../../quotes-ui/BRIEF-HTTP.md) asked for two things in
order: a characterization test that pins the real Week-1 API's contract,
green before any interceptor exists to consume it, and then three functional
interceptors wired against that same contract.

**The characterization test**,
[`api-contract.spec.ts`](../../quotes-ui/src/app/api-contract.spec.ts), pins three
real shapes — none guessed, all read from
[`EndpointExtensions.cs`](../../QuotesApi/Extensions/EndpointExtensions.cs) and
[`QuoteEndpointsTests.cs`](../../Quotes.Tests.Integration/QuoteEndpointsTests.cs):
`GET /api/quotes?page=N&size=N`'s `PagedResult<Quote>` envelope, `POST
/api/quotes`'s `400` as `ValidationProblemDetails` with capitalised error
keys, and `GET /api/quotes/{id}`'s `404` as a *plain* `ProblemDetails` with
no `errors` dictionary at all — a different 4xx shape from the 400, not a
variant of it. It was green on its own commit before a single interceptor
existed.

**Three interceptors**, ordered deliberately in `app.config.ts` — auth
header outermost, error mapping next (so it sees only the *final* error
after retries are exhausted, not one per attempt), retry-with-backoff
wrapping the per-attempt timeout innermost:

[`auth-header.ts`](../../quotes-ui/src/app/auth-header.ts) attaches `Authorization:
Bearer <token>` when a token exists, scoped to same-origin requests only —
the real Week-1 API checks no auth today (`QuotesApi/Program.cs` calls
neither `AddAuthentication` nor `AddAuthorization`), so this is forward
plumbing, not something exercised against a real 401.

[`error-mapping.ts`](../../quotes-ui/src/app/error-mapping.ts) maps a failed
response to a typed `AppError`, opt-in per request via an `HttpContext`
token rather than global — `httpResource`'s own `statusCode()`/`error()`
already drive `QuotesList` and `QuoteDetail`, and rewriting the thrown shape
under both would mean rewriting both to match. `QuotesApi.createQuote` opts
its `POST` in and now classifies `AppError` instead of parsing
`HttpErrorResponse` by hand.

[`retry-backoff.ts`](../../quotes-ui/src/app/retry-backoff.ts) retries a failed
idempotent GET with increasing backoff — and is where both of Day 15's bugs
were, described below.

**Two bugs, same file, same brief line the draft ignored — "retry idempotent
GETs."** `retry-backoff.spec.ts` was written against that line before the
interceptor existed. Same file, unchanged: **4 failures against the draft,
0 against the fix.**

The draft retried every method and every status — a failed `POST` exactly
like a failed `GET`, a `400` exactly like a `503`. A retried `POST` after a
lost response can create the quote twice, since a lost *response* to a
request the server already processed is indistinguishable, client-side,
from a request that never arrived; a retried `4xx` just resends a request
the server already rejected on purpose. Fixed with a method check and a
transient-failure check before any retry is attempted at all.

The second bug wasn't visible in the diff. rxjs's `retry({ delay })`
callback signature is `(error, retryCount)` — error first. The draft's
delay function took one parameter, so it silently received the
`HttpErrorResponse` as `retryCount`: arithmetic on an object is `NaN`, and a
timer scheduled for `NaN` fires on the next tick. A manual check — did it
retry? — would have said yes, with nothing about the five-line function
looking wrong. Only a fake-timer assertion that nothing had retried yet at
1ms of elapsed time caught it — found while writing the test, not while
reading the code.

Full write-up, including the states/edges table and what breaks if the
contract changes, in
[`VERIFICATION-HTTP.md`](../../quotes-ui/VERIFICATION-HTTP.md).
