[← Back to full README](../../README.md)

## Day 21 — HybridCache and stampede protection

Put `HybridCache` (in-process L1, Redis L2) in front of a hot read, so that a
cache miss under load issues one set of database queries rather than one per
concurrent request. Measure what changed.

**The read being cached is `GET /api/collections/summaries`** — the CQRS read
path from Day 12. It was the right target because it is the most expensive
read in the app that a screen actually calls on every page load: two queries,
one aggregating collections with their item counts and preview items, then one
fetching the quotes those previews reference. It is also a read whose answer
changes rarely and is identical for every caller asking the same question,
which is the entire precondition for caching something.
[`GetCollectionSummariesHandler`](../../QuotesApi/Features/Collections/GetCollectionSummaries.cs)
itself was not modified. Nothing about the query changed; only how often it
runs.

### Why HybridCache and not `IDistributedCache`

`IDistributedCache` would have given the Redis tier and nothing else. Three
things would still have been mine to write, and the third is the one that
matters:

1. **Two tiers by hand.** Check memory, miss, check Redis, miss, query, write
   both back, on every read.
2. **Serialisation by hand**, including deciding what happens when a payload
   is bigger than it should be.
3. **Stampede protection by hand** — and this is the exercise. `IDistributedCache`
   has no concept of "somebody else is already fetching this key". Every
   concurrent caller that misses goes to the database. The usual
   hand-rolled fix is a `SemaphoreSlim` per key in a
   `ConcurrentDictionary`, which is easy to write and easy to write *wrong*:
   the dictionary grows without bound unless entries are removed, and removing
   them races with the next caller acquiring the semaphore that is being
   removed.

[`HybridCache`](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid)
does all three, and gets the third one right by construction: concurrent
callers for the same key await one shared factory invocation.

### The wiring

[`CachingExtensions.AddQuotesCaching`](../../QuotesApi/Extensions/CachingExtensions.cs)
is the whole registration:

```csharp
if (!string.IsNullOrWhiteSpace(options.RedisConnectionString))
{
    services.AddStackExchangeRedisCache(redis =>
    {
        redis.Configuration = options.RedisConnectionString;
        redis.InstanceName = "quotes:";
    });
}

services.AddHybridCache(hybrid => hybrid.DefaultEntryOptions = entryOptions);
```

HybridCache picks up whatever `IDistributedCache` is in the container as its
L2 — there is no "use Redis" switch, registering Redis *is* the switch. **Which
means the Redis tier is optional in the only way that counts: if no connection
string is configured, nothing registers an `IDistributedCache`, and HybridCache
runs L1-only and still deduplicates stampedes.** That is the same refusal to
hard-depend on a live backing service that kept Day 19's publisher out of this
host and made Day 20's relay a separate process. An API that will not start
because a cache is down has turned an optimisation into an outage.

Redis for local runs is [`redis/docker-compose.yml`](../../redis/) — four lines
of service definition, no persistence, no password, because everything in it is
reconstructible from the database by definition.

**The read**, in
[`CachedCollectionSummaryReader`](../../QuotesApi/Features/Collections/ICollectionSummaryReader.cs):

```csharp
return await cache.GetOrCreateAsync(
    CacheKeys.CollectionSummaries(ownerId, previewSize),
    (mediator, metrics, ownerId, previewSize),
    static async (state, token) =>
    {
        state.metrics.RecordFactoryInvocation();
        var summaries = await state.mediator.Send(
            new GetCollectionSummariesQuery(state.ownerId, state.previewSize), token);
        return summaries.ToArray();
    },
    entryOptions,
    CacheKeys.CollectionSummariesTags,
    ct);
```

Three deliberate details. The `TState` overload with a `static` lambda means
the factory delegate is allocated once for the application rather than once per
request — which matters *because* the cache works: once hits dominate, what the
cache itself allocates on every read is a real share of what is left. The key
includes `previewSize`, because a key that under-describes its value is how a
request for three preview items gets served the cached answer for one. And the
factory materialises to an array, so the cached type is concrete: HybridCache
serialises anything it cannot prove immutable, *including on the L1-only path*,
so the round-trip is real and is worth not resting on interface
reconstruction.

### Proving the stampede protection

The claim is that N concurrent misses produce one database fetch. Asserting
that with a mocked cache would be asserting that the mock deduplicates, which
proves nothing — so
[`CollectionSummaryCacheTests`](../../Quotes.Tests.Unit/CollectionSummaryCacheTests.cs)
resolves a **real** `HybridCache` out of a real `ServiceCollection`, the same
choice Day 20 made in using a real SQLite file rather than EF's InMemory
provider.

The test that carries the claim launches 50 concurrent readers against a cold
cache, then **waits until one of them is provably inside the factory before
letting the factory return**:

```csharp
await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
gate.Release();
```

Without that gate the first caller would finish and populate the cache before
the rest were even scheduled, and the test would pass against a warm cache
while proving nothing about concurrency. With it, all 50 were in flight against
an empty cache at the same instant, and the assertion is that the query ran
exactly once.

Four more tests cover the parts that make it a cache rather than a bug: a
second read comes back through the serialiser intact, a different `previewSize`
is a different entry, an invalidation forces the next read back to the
database, and with the cache disabled every read hits the database — the
"before" side of the measurement asserted rather than assumed.

### Invalidation

Every write path on
[`CollectionsController`](../../QuotesApi/Controllers/CollectionsController.cs)
calls `RemoveByTagAsync` through
[`ICollectionSummaryCacheInvalidator`](../../QuotesApi/Caching/ICollectionSummaryCacheInvalidator.cs).
Invalidation lives in the controller rather than in the command handlers
because **the three write paths do not share one chokepoint**: create and
add-quote go through MediatR, remove-quote goes straight to the repository. A
handler-level invalidation would have silently missed the third, which is
exactly the bug that surfaces as a stale screen nobody can reproduce. Once
remove-quote moves onto MediatR, a notification handler is the better home and
the controller can go back to not knowing a cache exists.

**Deleting a quote invalidates too**, and that one is easy to miss. A summary
embeds the author and text of the quotes in its preview, and the handler drops
preview entries whose quote no longer exists - so a deleted quote keeps
appearing on the collections screen until the entry expires unless
`DELETE /api/quotes/{id}` invalidates as well. Creating a quote deliberately
does *not* invalidate: a new quote is in no collection yet, so it cannot appear
in any preview, and throwing away a warm cache for a write that provably cannot
have changed what it holds is pure loss. Working out which writes can actually
change a cached answer - rather than invalidating on all of them or on the
obvious ones only - is most of the work of not having a stale cache.

Two things about tag invalidation in HybridCache are worth stating because they
are not obvious. It is **logical, not physical**: `RemoveByTagAsync` records
"ignore anything with this tag created before now" rather than walking L1 and
Redis deleting keys. That is why invalidating on every write is cheap no matter
how many keys exist — and also why the memory is not reclaimed at invalidation
time, so a high write rate against a long `Expiration` leaves more dead payloads
in Redis than the word "remove" suggests. And it invalidates *all* cached
summary variants at once rather than reasoning about which keys a given write
touched, because a new collection changes the unfiltered list and its owner's
filtered list both.

### One thing caching changed that is not performance

`GET /api/collections/summaries` is anonymous, and both of its parameters
become part of a cache key. Uncached, a caller passing a thousand different
`ownerId` values bought a thousand queries and nothing else. Cached, it also
buys a thousand resident entries - in memory, and in Redis when configured -
that nothing evicts before their expiry. The caching is what turned a load
problem into a memory-growth one, so
[`CollectionsController`](../../QuotesApi/Controllers/CollectionsController.cs)
now bounds `ownerId` length and clamps `previewSize` to 1-25 before either
reaches the reader. `previewSize` is clamped rather than rejected because it
feeds a `Take()` in the query, so an absurd value was already a real cost on
the database.

This is the sort of thing that makes a cache more than a speed change: it gave
an existing anonymous endpoint a new resource an attacker can grow, and the
guard belongs with the commit that introduced it rather than in a later one
that fixes it.

### Measuring it

Latency alone would not answer the question, because the exercise is about
**database load**, and a p99 improvement does not tell you whether the database
was asked 1 time or 400.
[`DbCommandCounterInterceptor`](../../QuotesApi/Caching/DbCommandCounterInterceptor.cs)
counts every command EF actually executes. A cache reporting its own hit rate
is the least trustworthy witness available — if the caching layer had a bug
that let requests through, its own counters would be the last place it showed
up — so the number that carries the claim is measured at the EF boundary, not
inferred from what the cache believes about itself.

`GET /api/cache/stats` exposes the counters; `POST /api/cache/reset` zeroes
them between runs. Reset deliberately does *not* clear the cache: a run that
reset both could not tell "the cache was warm and served everything" apart from
"the cache was empty and one factory call served everything", and those are
different results.

Because `Cache:Enabled` is configuration, the before and after are **the same
build with a different setting** — the only way the two numbers are comparable
at all.

```powershell
# before
$env:Cache__Enabled = "false";  dotnet run --project QuotesApi
k6 run --env SCENARIO=sustained perf/cache-load-test.js

# after (restart the API between runs so the burst starts cold)
$env:Cache__Enabled = "true";   dotnet run --project QuotesApi
k6 run --env SCENARIO=stampede  perf/cache-load-test.js
k6 run --env SCENARIO=sustained perf/cache-load-test.js
```

The two scenarios answer different questions and are run separately so they
cannot corrupt each other's numbers. `stampede` is 200 VUs released together
against a cold cache and the number that matters is EF commands. `sustained`
holds a fixed **arrival rate** for 30s — not a fixed VU count, because a fixed
VU count would let the faster cached run offer more load and quietly compare
two different experiments.

### Results

Measured on SQLite, one API instance, Redis in Docker, k6 2.2.0 on the same
machine. `Cache:Enabled` was the only difference between the runs.

**Stampede — 200 concurrent requests against a cold cache**

| | EF commands | Factory invocations | Hit rate | p50 | p99 | Failed |
|---|---|---|---|---|---|---|
| Cache on (L1 + Redis) | **2** | **1** | 99.50% | 1.99 s | 2.43 s | 0% |

**200 concurrent readers, one database fetch.** The read issues two queries, so
2 EF commands is one execution of it and nothing else. Every one of the other
199 requests was served from the single in-flight factory call rather than
starting its own.

The uncached row is not in this table because it was not run — but the
extrapolation is not a guess either: the sustained run below measured exactly
2.00 EF commands per read with the cache off (1,524 / 762), so 200 concurrent
uncached readers would issue on the order of 400 commands rather than 2.

The ~2 s latency here is not the cost of a cache miss. This burst hit an API
that had just restarted, so the one factory call also paid EF model building,
JIT and the first database connection, and the other 199 waited on it. `min`
was 7.17 ms — a straggler that arrived after the entry was published. Read
the other way, this is the protection working: **199 requests did not each pay
that cold-start cost concurrently**, which is what the sustained run below
shows happening when they do.

**Sustained — 200 req/s offered for 30 s**

| | EF commands | EF commands/sec | Hit rate | p50 | p95 | p99 | Completed | Failed |
|---|---|---|---|---|---|---|---|---|
| Cache off | 1,524 | 50.8 | 0% | — | 23.95 s | 26.50 s | 762 | 67.58% |
| Cache on (L1 + Redis) | **0** | **0.00** | 100% | 4.12 ms | 66.73 ms | 131.68 ms | 6,001 | 0% |

**Zero database commands across 6,001 requests.** The 30 s window fits inside
the 5-minute `Expiration`, so after the burst above populated the entry, the
sustained run never needed the database at all.

**The uncached run did not slow down — it fell over.** It never reached the
offered rate: k6 exhausted its 400 VUs, dropped 3,643 iterations, and 67.58%
of requests were refused outright at the socket. Of the 2,360 that connected,
762 succeeded, averaging 17.52 s. So its p95 and p99 are measurements of a
queue, not of how long this endpoint takes to answer — the same reading Day 11
made of its own flat percentiles, and the reason the `—` in the p50 column is
honest: k6 records 0 s for a request that never connected, so a p50 of "0s"
there describes failures, not fast responses.

The cached run served the full 200 req/s using a **maximum of 12 concurrent
VUs**, against 400 saturated without it. That ratio is the real result: the
same offered load, answered out of memory, needing roughly 3% of the
concurrency and none of the database.

Not run: the L1-only rows. Redis was configured for both runs above, so what
is measured here is the two-tier configuration. Since a warm L1 answers before
L2 is consulted, the sustained numbers would be expected to look much the same
without Redis — but that is reasoning, not a measurement, so it is not in the
table.

### What this deliberately does not do

**Stampede protection is per process, not per cluster.** HybridCache
deduplicates concurrent callers within one instance. Two API replicas that miss
at the same moment each run the factory once — two database hits, not one — and
sharing Redis does not change that, because the coordination is in-process and
the L2 is only storage. For N replicas the worst case is N concurrent factory
runs instead of N × concurrency, which is still the difference between a bad
second and an outage, but it is not one. Making it exactly one needs a
distributed lock, which buys a new failure mode (what happens when the lock
holder dies) in exchange.

**The cache is not part of the integration suite.** No existing integration
test drives `GET /api/collections/summaries` over HTTP, so enabling the cache
did not change what they assert. The cache's own behaviour is covered by unit
tests against a real `HybridCache` instead. A test that ran the full HTTP stack
with Redis attached would be worth having and is not here.

**One `HybridCache` per process, so L1 memory is not bounded by Redis.** L1
entries live in this process's `MemoryCache` and are capped only by
`LocalCacheExpiration` and the key bounds above, not by any size limit
configured here. A production deployment would want `MaximumPayloadBytes`
thought about and a memory cache size limit set.

**Nothing measures Redis being down mid-run.** `abortConnect=false` in the
documented connection string stops a missing Redis from failing startup, but
what a *mid-request* L2 failure does to a response is untested. That is the
honest gap: the failure mode most likely to matter in production is the one
this exercise did not reproduce.

**No cache warming and no negative caching.** The first request after every
deploy or expiry pays full cost, and a query returning zero collections is
cached like any other, so a burst of requests for a nonexistent owner is cheap
by accident rather than by design.

### Verification status

`dotnet build` is clean, `dotnet test Quotes.Tests.Unit` passes 199/199 - 194
before this exercise plus the five in `CollectionSummaryCacheTests` - and
`dotnet test Quotes.Tests.Integration` passes 54/54 unchanged. That the
integration count did not move is the point: the cache is on by default in
that host now, and no existing assertion changed its answer. The
measurements above are from real runs on
2026-09-03 against SQLite with Redis in Docker, and the counter lines come
from `GET /api/cache/stats`, which counts EF commands at the database
boundary rather than asking the cache to report on itself.

The load test independently exercises two things the unit tests also cover:
every one of the 6,001 cached responses passed k6's "body is a JSON array"
check, so the `CollectionSummary` round-trip through HybridCache's serialiser
works against the real thing; and the 200-to-1 stampede result is the same
guarantee `ConcurrentReadsOnAColdCache_RunTheQueryOnce` asserts, observed over
HTTP rather than in-process. What the load test does *not* exercise is
invalidation - `AfterInvalidation_TheNextReadGoesBackToTheDatabase` is the only
thing covering that, which is why it is a test and not a paragraph.

Two package versions were the one piece of friction on first build:
`Microsoft.Extensions.Caching.Hybrid` 10.9.0 and
`Microsoft.Extensions.Caching.StackExchangeRedis` 10.0.10 both restore
correctly, but a stale `obj/` from before they were added will fail to compile
with "IServiceCollection does not contain a definition for AddHybridCache" -
the assemblies resolve in `project.assets.json` while the compiler is still
working from the pre-restore evaluation. `dotnet clean`, delete `QuotesApi/obj`,
then `dotnet restore` and `dotnet build --no-restore` separately.
