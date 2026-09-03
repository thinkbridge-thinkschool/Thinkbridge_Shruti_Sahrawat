[← Back to full README](../../README.md)

## Day 22 — Resilience with Polly

Wrap an outbound dependency with Polly: retry-with-backoff (idempotent only),
a circuit breaker, a timeout, and a bulkhead. Prove the circuit opens under
sustained failure and recovers.

**There was no genuine third-party HTTP dependency in this codebase to wrap.**
Day 5 already put a named `HttpClient` behind retry, a circuit breaker and a
per-attempt timeout - `GET /api/demo/resilience` points at `localhost:9`
specifically to force failures, since nothing real was being called. Rather
than repeat that shape, Day 22 promotes it to a proper typed client
([`IUpstreamClient`](../../QuotesApi/Resilience/UpstreamClient.cs)) against a
small in-repo stub upstream whose failures are switched on and off at
runtime (healthy / failing / slow). Real sockets, a real `HttpClient`
pipeline, no internet dependency, and - the part that matters for this
exercise - a circuit breaker's full lifecycle needs failure to start and stop
on command, which no genuine third party offers on demand and this stub does.
Day 5's own write-up recorded a gap this exercise closes directly: an
18-second request under what it called a "10-second timeout", because
`AddTimeout` there bounded one attempt, not the whole operation. Day 5's
wiring is left in place in `Program.cs`, unused, because its own tests and
write-up still reference it - see the comment there for why keeping dead code
was the right call for once.

### The pipeline, outermost to innermost

[`OutboundResiliencePipeline.Configure`](../../QuotesApi/Resilience/OutboundResiliencePipeline.cs)
is the one place all five strategies are assembled, and it is the same method
both `Program.cs` and the unit tests call - a test that reassembled its own
copy of the chain would only prove that copy works, which is not a claim
anyone needs. The order is the same one
[`AddStandardResilienceHandler`](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience)
uses, and each position is load-bearing:

1. **Bulkhead** (`AddRateLimiter`, a concurrency limiter). Outermost, so a
   rejection costs nothing - no socket, no timer, no retry budget spent on a
   dependency the bulkhead has already decided not to trouble. 8 concurrent
   calls, queue limit 0: a caller past the limit is refused immediately rather
   than queued, since a queue in front of a saturated dependency just turns a
   fast rejection into a slow one.
2. **Total request timeout** (`AddTimeout`, 8s). Outside the retry, on
   purpose - this is Day 5's fix. A timeout placed inside the retry loop
   bounds one attempt; four attempts and their backoff can still exceed it
   without any single attempt crossing the line. This is the caller's actual
   budget, retries included.
3. **Retry** (`AddRetry`, up to 3 attempts, exponential backoff, jitter),
   gated on idempotency - see below.
4. **Circuit breaker** (`AddCircuitBreaker`). Inside the retry, so it sees
   attempts, not calls: one failing idempotent call already contributes up to
   4 executions toward `MinimumThroughput`. Sharing
   [`TransientHttpFailure.Matches`](../../QuotesApi/Resilience/TransientHttpFailure.cs)
   with the retry is deliberate - if the two disagreed on what counts as a
   failure, the breaker could open against calls the caller saw succeed, or
   never see the failures the retry was busy hiding.
5. **Attempt timeout** (`AddTimeout`, 2s). Innermost, bounds one HTTP attempt
   and nothing else - and its own failure counts toward the breaker, because a
   dependency that never answers is failing even though it never returned a
   status code.

### Retry is idempotent, not "safe"

[`RetryEligibility.IsRetryable`](../../QuotesApi/Resilience/RetryEligibility.cs)
retries GET, HEAD, OPTIONS, TRACE, PUT and DELETE - every method RFC 9110
classifies as idempotent - plus POST when the request carries an
`Idempotency-Key` header. This is deliberately not the framework's own
`DisableForUnsafeHttpMethods()` helper, which gates on *safety*, not
*idempotency*, and refuses to retry PUT and DELETE even though repeating
either lands on the same end state. The exercise asked for idempotent; the
shipped helper gives you safe. `ADelete_IsRetried_WhichTheFrameworkSafetyHelperWouldNotDo`
in the test file exists specifically to pin that difference down.

The POST escape hatch restates Day 20's contract on the client side. The
outbox pattern made at-least-once delivery safe by giving every message a
`MessageId` and having the consumer deduplicate on it; a POST carrying an
`Idempotency-Key` is a caller making the identical claim - "the server knows
how to recognise a repeat of this" - and the retry trusts it. Two honest
limits: this cannot detect a server that ignores the key and duplicates
anyway, and it cannot replay a request whose body is a non-buffered stream,
which is not a problem for this codebase's small JSON bodies and would be the
first thing to bite a larger one.

### Proving it - real output, not a description of expected behaviour

[`perf/breaker-timeline.ps1`](../../perf/breaker-timeline.ps1) drives the
running API through all four primitives in one pass and prints a timestamped
log. Run against the API fresh off `dotnet run`, 2026-09-03:

```
==============================================================================
  1. Baseline - the dependency is healthy
==============================================================================
[22:28:00.300] call 1  ->  HTTP 200  Success  (323ms)
[22:28:00.415] call 2  ->  HTTP 200  Success  (108ms)
[22:28:00.462] call 3  ->  HTTP 200  Success  (44ms)
[22:28:00.475] circuit=Closed  upstream received 3 requests
==============================================================================
  2. Retry is idempotent-only
==============================================================================
[22:28:00.577] POST with no key      -> HTTP 502  UpstreamFailure  (62ms)
[22:28:00.580]   upstream saw 1 request, retries suppressed: 1
[22:28:01.779] POST with a key       -> HTTP 502  UpstreamFailure  (1173ms)
[22:28:01.781]   upstream saw 4 requests, retries taken: 3
==============================================================================
  3. Bulkhead - 12 concurrent calls, 8 permits
==============================================================================
[22:28:03.539] 8 x HTTP 200  (accepted and served)
[22:28:03.540] 4 x HTTP 429  (rejected by the bulkhead)
[22:28:03.558] all 12 returned in 1675ms; upstream received 8 of them
==============================================================================
  4. Circuit breaker - sustained failure, then recovery
==============================================================================
[22:28:04.448] call 1  ->  HTTP 502  UpstreamFailure    (841ms)  circuit=Closed  upstream=4
[22:28:04.828] call 2  ->  HTTP 503  ShortCircuited     (369ms)  circuit=Open    upstream=6
[22:28:04.839]   >> circuit OPEN after 2 call(s); the dependency had seen 6 requests
[22:28:04.866] call 3  ->  HTTP 503  ShortCircuited      (11ms)  circuit=Open    upstream=6
   ... calls 4-8, all ShortCircuited, upstream held at 6 ...
[22:28:05.035] while open: 7 call(s) refused locally, upstream still at 6 requests
[22:28:05.055] waiting out the 5s break, then making the dependency healthy again
[22:28:10.656] probe 1  ->  HTTP 200  Success  (67ms)   circuit=Closed  upstream=7
[22:28:10.700] probe 2  ->  HTTP 200  Success  (30ms)   circuit=Closed  upstream=8
[22:28:10.752] probe 3  ->  HTTP 200  Success  (36ms)   circuit=Closed  upstream=9
```

**Idempotency, measured at the dependency, not asserted about the client.**
The bare POST reached the stub exactly once and was not retried; the same
POST carrying a key reached it 4 times (3 retries) before the retry budget
gave up. Same endpoint, same 503, the only difference is what the request
claimed about itself.

**Bulkhead, exact.** 12 concurrent calls against 8 permits and a queue limit
of 0 produced exactly 8 accepted and 4 rejected - not approximately, exactly,
because a `ConcurrencyLimiter` with no queue is a hard admission cutoff. The
accepted calls took the full 1.5s the stub was told to delay before
answering; the rejected ones never reached it at all.

**The circuit opening, reconstructed by hand from the counters, matches
Polly's own accounting exactly.** Three phases' worth of prior traffic
(3 successes, 5 failures from the idempotency proof, 8 more successes from
the bulkhead calls) had already put 16 executions and 5 failures into the
breaker's 30-second window before phase 4 started - which is why
`MinimumThroughput` is 20 rather than the 8 a single isolated test needed
(more on that below). Call 1's 4 failing attempts bring the window to 20
executions, 9 failures - a 45% ratio, still under the 50% threshold, so the
circuit stays closed and call 1 surfaces as a genuine `UpstreamFailure`. Call
2's first attempt makes it 21/10 (47.6%, still closed); its second attempt
makes it 22/11 - exactly 50%, exactly 20 executions - and the breaker opens
**mid-retry**. The retry loop, unaware anything changed, still decides to
attempt a third time; that attempt hits the now-open circuit before it can
reach the dependency at all, so call 2's own final result is `ShortCircuited`
even though two of its attempts were real, failed, counted requests. That is
also the exact accounting behind "retries taken: 5" for this phase: 3 for
call 1's four attempts, 2 for call 2 - one that reached the dependency and
one that didn't, because the circuit opened underneath it between the
decision to retry and the attempt itself. Calls 3 through 8 never reach the
dependency - the upstream counter holds at 6 for the rest of the phase,
which is the entire point of a breaker: not a faster failure, a spared
dependency.

**Recovery is a single probe, not a burst.** After the 5-second break plus
buffer, the next call is let through, succeeds, and the circuit closes
immediately - `CircuitState.HalfOpen` never gets more than one chance before
Polly decides.

### What broke on the first pass, and why it is in this write-up

The first run of this exact script produced a circuit that was already open
before phase 4 began, and a bulkhead phase whose "accepted" calls came back
in 200ms instead of the expected 1.5s. Neither was a pipeline bug. Phase 2
deliberately makes the dependency fail, to prove the retry gate - and those
failures land in the *same* rolling window phase 3 and phase 4 also use,
because Polly's breaker has no concept of "phases", only of attempts.
`MinimumThroughput: 8` was sized for one isolated unit test making one
isolated call; against a script's cumulative traffic it tripped during phase
2 and stayed tripped, so phase 3's "bulkhead" calls were actually being
short-circuited (hence 503s that returned instantly instead of 200s that took
1.5s), and phase 4 opened on data nobody could see, because the failures that
tripped it had already happened off-screen. Raised to 20 - comfortably above
what phases 1 through 3 can contribute (16, worked out above), comfortably
below what phase 4's dedicated failures reach within a couple of calls - and
the second run is the one quoted above, self-consistent down to the last
retry. Left in as a finding rather than quietly fixed, because "the demo
script itself needs tuning against the thing it demonstrates" is a real
lesson about testing anything stateful across multiple phases, not a mistake
worth hiding.

### What this deliberately does not do

**One shared breaker, one shared window, for the whole process.** Every
caller of `IUpstreamClient` trips and recovers together; there is no
per-caller or per-route isolation. For one dependency behind one client, that
is correct - Polly's own multi-dependency guidance is to give each dependency
its own named pipeline, which this codebase does not need because it has
exactly one outbound dependency to protect.

**The idempotency gate trusts the caller.** A POST that claims an
`Idempotency-Key` but talks to a server that ignores it will be retried into
duplicate side effects, and nothing here can detect that from the client
side. This is the same trust boundary Day 20's outbox consumer sits on the
other side of.

**Nothing here is load-tested.** Day 21 measured throughput and p99 under
k6; this exercise proves *correctness* of a state machine (closed, open,
half-open, closed) and a gate (idempotent, not-idempotent), not performance
under sustained concurrent load. The bulkhead's 8/4 split above is a
correctness check on one burst, not a capacity number.

**The stub is a stand-in, and known to be one.** `UpstreamFaultSwitch` proves
the pipeline behaves correctly against controllable failure; it says nothing
about how a *real* dependency fails in practice - partial responses, slow TLS
handshakes, DNS flapping. The pipeline is real and does not know it is
talking to a stub, which is the most a same-process fault injector can
honestly claim.

### Verification status

`dotnet build` is clean. `dotnet test Quotes.Tests.Unit` passes 208/208 - 199
from before this exercise plus the nine in
[`OutboundResilienceTests`](../../Quotes.Tests.Unit/OutboundResilienceTests.cs)
covering each primitive against the real pipeline (not a reimplementation of
it): idempotent retry, suppressed retry on a bare POST, retried POST with a
key, retried DELETE, no retry on a 400, the full breaker lifecycle
(closed → open → half-open → closed, with the dependency proven uncalled
while open), bulkhead rejection, the total budget bounding the whole
operation rather than one attempt, and the config-time rejection of an
attempt timeout that isn't smaller than the total. `dotnet test
Quotes.Tests.Integration` passes 54/54, unchanged - this exercise added no
HTTP-level behaviour to any existing endpoint, so nothing there had reason to
move.

One test failure on the way here, fixed rather than worked around: the total-
budget test originally set `TotalRequestTimeout` to an exact multiple of
`AttemptTimeout` (800ms = 4 × 200ms), which put the total deadline and an
in-flight attempt's own deadline on the same instant. Polly's inner timeout
strategy checks whether the ambient token was *already* cancelled before its
own timer fired to decide whether to claim a cancellation as its own; at a
genuine tie, that check can go either way, so the attempt sometimes claimed
it and retried again instead of letting the total budget end the operation -
a flaky assertion, not a pipeline defect. Moving the total timeout to 900ms,
comfortably mid-attempt rather than on a boundary, removed the tie entirely.

Real numbers throughout: the breaker-lifecycle output above is a real run
against the real stub, and the counters in it were independently reconstructed
by hand from Polly's own documented threshold rules and matched exactly - the
kind of check that catches a plausible-looking but wrong number, which a
glance at "yes it opened, yes it recovered" would not have caught.
