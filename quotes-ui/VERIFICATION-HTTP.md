# Verification log — HttpClient + functional interceptors

Day 15. What was pinned before any UI, what came back wrong, and what
breaks if the Week-1 contract changes.

## How this was verified

Everything below runs against `HttpTestingController`, not a live Week-1
API — there is no live instance in this environment. What that does and
does not prove: it proves the interceptors classify and retry exactly the
response bodies this API actually sends (each one transcribed from
`EndpointExtensions.cs` and cross-checked against
`Quotes.Tests.Integration/QuoteEndpointsTests.cs`, not invented). It does
not prove the retry interceptor behaves correctly against a real flaky
network, only against a mocked one that fails and then succeeds on command.

Backoff timing is asserted with Vitest's fake timers
(`vi.useFakeTimers()` / `vi.advanceTimersByTimeAsync`) rather than real
delays — a test that actually waited 300ms, 600ms, 1200ms per case would
be slow and, worse, would pass by accident if the delay were wrong by a
constant factor. Fake timers make "has not retried yet at 1ms" and "has
retried once the real delay has elapsed" both exact.

## Step 1 — the characterization test

[`api-contract.spec.ts`](src/app/api-contract.spec.ts), three cases, green
from the first commit, before `auth-header.ts`, `error-mapping.ts`, or
`retry-backoff.ts` existed:

| Real endpoint | Real response | What's pinned |
|---|---|---|
| `GET /api/quotes?page=1&size=10` | `200`, `PagedResult<Quote>` | envelope field names (`items`/`page`/`size`/`totalCount`), item shape (`id`/`author`/`text`/`createdAt`), `createdAt` arrives as a string |
| `POST /api/quotes`, invalid body | `400`, `ValidationProblemDetails` | `errors` keyed by capitalised C# property names — `Author`, not `author` |
| `GET /api/quotes/{id}`, missing id | `404`, plain `ProblemDetails` | no `errors` dictionary at all — a different 4xx shape from the 400, not a variant of it |

## Step 2 — states and edges exercised

| State / edge | How it was forced | Result |
|---|---|---|
| GET succeeds | flush 200 with the real envelope | resolves with the typed `PagedResult<Quote>` |
| GET fails transiently, then succeeds | flush 503, advance the backoff timer, flush 200 | resolves — the retry is invisible to the caller |
| GET exhausts its retries | flush 503 four times running out the timer between each | rejects with the final `HttpErrorResponse`, status 503 |
| GET fails with 400 | flush 400 | rejected immediately, no retry issued |
| GET fails with 404 | flush 404 | rejected immediately, no retry issued |
| POST fails with 503 | flush 503 on a POST | rejected immediately, no retry issued — this is finding 1 |
| Auth header, no token | `AuthTokenStore.token()` is `null` | no `Authorization` header sent |
| Auth header, token set | `token.set('abc123')` | `Authorization: Bearer abc123` on a same-origin request only |
| Auth header, cross-origin request | token set, request to `fonts.googleapis.com` | no `Authorization` header — see "the real risk" below |
| Error mapping, 400 | `MAP_ERRORS` set, flush `ValidationProblemDetails` | `AppError` with `kind: 'validation'`, `fieldErrors` keyed exactly as the server sent them |
| Error mapping, 404 | `MAP_ERRORS` set, flush plain `ProblemDetails` | `AppError` with `kind: 'notFound'`, message from the server's own `detail` |
| Error mapping, 5xx | `MAP_ERRORS` set, flush 503 | `AppError` with `kind: 'server'`, a friendly message with no status code leaked into the text |
| Error mapping, network failure | `MAP_ERRORS` set, simulate a status-0 error | `AppError` with `kind: 'network'`, distinct from `'server'` |
| Error mapping, not opted in | no `MAP_ERRORS`, flush 500 | unchanged raw `HttpErrorResponse` — `httpResource` callers are untouched |
| `QuoteForm`'s real POST path | full 22-test suite, now wired through `errorMappingInterceptor` | unchanged behaviour, unchanged assertions — the refactor didn't move where the diff should show up |

**40 tests total, all green:** 3 characterization + 4 auth-header + 5
error-mapping + 6 retry-backoff + 6 quote-detail + 16 quote-form.

## Two things the agent got wrong, both in the same file

The spec in `retry-backoff.spec.ts` was written against the brief — "retry
idempotent GETs with backoff" — before the interceptor existed. Same file,
unchanged: **4 failures against the draft, 0 against the fix.**

**One — retried every method and every status.** The draft was
`next(req).pipe(retry({ count: 3, delay: backoffDelay }))` with no
condition at all: a failed `POST /api/quotes` got retried exactly like a
failed `GET`, and a `400` got retried exactly like a `503`. The brief's own
wording — "idempotent GETs" — was right there; the draft implemented
"anything that fails." A retried `POST` after a lost response is a
duplicate quote the server has no way to know is unwanted: the first
attempt may have already succeeded, and nothing on the wire distinguishes
that from the request never arriving. A retried `400` just spends a
round-trip resending a request the server has already told you is wrong,
delaying the real, useful error. Fixed with an early return for anything
but `GET`, and `backoffDelay` now aborts the retry (via `throwError`)
immediately for any error that isn't a network failure or a 5xx.

**Two — the backoff had no actual delay, and nothing about the draft
looked wrong.** This one wasn't visible in the diff at all — I found it
writing the fake-timer test, not reading the code. rxjs's
`retry({ delay })` callback signature is `(error, retryCount) => …` — error
first. The draft's `backoffDelay(retryCount)` took one parameter, so at
call time it silently received the `HttpErrorResponse` as `retryCount`.
`BASE_DELAY_MS * 2 ** (error - 1)` is arithmetic on an object: `NaN`. A
timer scheduled for `NaN` behaves like a timer scheduled for `0` — it fires
on the next tick. Manually watching it "retry" would have looked completely
correct: a failed request, followed shortly by a second one that succeeds.
What that manual check can't see is *how* shortly — every retry fired
essentially instantly, with no backoff at all, which under a real outage
means hammering a struggling server three times as fast as it's failing
rather than backing off from it. Caught by asserting nothing had retried
yet after 1ms of fake time — a check no amount of reading the interceptor's
five lines would have surfaced, because the bug isn't in what the code
says, it's in what rxjs does with the arguments it's actually given.

## What breaks if the API contract changes

**A new required field is added server-side.** Every `POST` starts 400ing
with an `errors` key the client's `AppError` mapping doesn't specifically
know about — but it doesn't need to: `toAppError` reads whatever keys the
`errors` dictionary actually has, so the new field's message reaches
`fieldErrors` and, if `QuoteForm` doesn't have a matching input, surfaces
in the banner rather than vanishing (the same `unattached` path Day 14
built for exactly this). The characterization test would keep passing —
it only pins the fields that exist today — so this is a case the contract
test does not catch and the error-mapping design absorbs instead.

**The 404 gains an `errors` dictionary of its own** (say, a future
`GetById` also validates the id format and folds that into
`ValidationProblemDetails` instead of a plain `ProblemDetails`). The
characterization test's third case pins `error.error.errors` as
`undefined` today; it would need updating, and until it is,
`toAppError`'s `kind: 'notFound'` branch would keep reading only `detail`
and silently drop the new field errors — a real, if narrow, regression the
test is specifically positioned to catch first.

**Retry-worthy failures start returning a 200 with an error body** instead
of a real HTTP error status (some APIs do this to work around
retry/interceptor layers exactly like this one). `isTransient` checks
`error.status`, which only exists on a thrown `HttpErrorResponse` — a 200
never throws, so it would never retry and never get classified as an
`AppError` either. Both interceptors would go silent on a failure mode
they were built to handle, which is the reason this API returning real
HTTP status codes for real HTTP failures is worth treating as load-bearing,
not incidental.

**Auth becomes real.** `AuthTokenStore` and `authHeaderInterceptor` are
already correct plumbing for that day — same-origin scoped, header set
only when a token exists — but nothing in this codebase sets `token()`
today, because there is no login flow and the real API checks no
`Authorization` header (`QuotesApi/Program.cs` has no
`AddAuthentication`/`AddAuthorization` at all). The day that changes, every
request this app makes would start getting a 401 it has no typed handling
for yet — `toAppError`'s `client` branch would catch it generically, with a
message too vague to tell a user "log in again."
