# Day 10 — EF Core change tracker and AsNoTracking

Identity resolution, tracked versus untracked reads, and the read-path cost of the change tracker, measured with BenchmarkDotNet.

Harness: [`Program.cs`](Program.cs), referencing the real `QuotesDbContext` and reading `QuotesApi/quotes.db` seeded to 10,000 rows ([`../sql/seed-10k.sql`](../sql/seed-10k.sql)).

```bash
cd Quotes.Benchmark
dotnet run -c Release            # the benchmark
dotnet run -c Release -- --demo  # the behavioural differences
```

## The two query variants

```csharp
// Tracked (the default)
var rows = await ctx.Quotes.Take(Rows).ToListAsync();

// Untracked
var rows = await ctx.Quotes.AsNoTracking().Take(Rows).ToListAsync();
```

## What the change tracker does

When EF materialises an entity it does three things: creates the object, stores a snapshot of every property value, and registers the entity in the context's identity map keyed by primary key. The snapshot is how `SaveChanges` knows what changed — it compares current values against it. The identity map is how EF guarantees one object per key per context.

`AsNoTracking` skips the second and third. You get the entities and nothing else.

## Benchmark design

Two decisions worth stating, because they change what the numbers mean.

**A fresh `DbContext` per iteration.** Reusing one would leave the identity map populated after the first iteration, so subsequent tracked runs would find entities already resolved and measure a warm cache rather than the cost of tracking. That would flatter `AsNoTracking` for the wrong reason. The cost is that context creation is included in both variants — but it is identical in both, so it raises the absolute numbers without distorting the comparison.

**`[Params(100, 1000, 10000)]`.** This is not padding. If tracking overhead is genuinely per-entity, the *relative* gap should hold roughly constant across row counts. That was the hypothesis, and testing it is the point.

## Results

`BenchmarkDotNet v0.15.8`, .NET 10.0.10, 12th Gen Intel Core i5-1235U, Windows 11. 15 iterations, 3 warmup runs.

| Method | Rows | Mean | Ratio | Gen0 | Gen1 | Gen2 | Allocated | Alloc Ratio |
|---|---|---|---|---|---|---|---|---|
| Tracked | 100 | 677.8 µs | 1.09 | 27.3 | 3.9 | – | 176.60 KB | 1.00 |
| AsNoTracking | 100 | 374.4 µs | 0.60 | 19.5 | 2.0 | – | 120.79 KB | **0.68** |
| Tracked | 1,000 | 2,885.9 µs | 1.00 | 195.3 | 113.3 | – | 1,205.23 KB | 1.00 |
| AsNoTracking | 1,000 | 1,971.1 µs | 0.69 | 101.6 | 35.2 | – | 638.91 KB | **0.53** |
| Tracked | 10,000 | 45,891.8 µs | 1.01 | 1750.0 | 1000.0 | 250.0 | 11,427.25 KB | 1.00 |
| AsNoTracking | 10,000 | 24,952.9 µs | 0.55 | 1000.0 | 666.7 | 266.7 | 5,916.85 KB | **0.52** |

At 10,000 rows: **roughly twice as fast and half the allocations** — 11.4 MB against 5.9 MB per query.

### The hypothesis was wrong, usefully

The prediction was that the allocation ratio would stay roughly constant if tracking cost is per-entity. It does not: **0.68 at 100 rows, 0.53 at 1,000, 0.52 at 10,000.**

The explanation is that every query pays fixed costs regardless of row count — opening a connection, building the command, resolving the model. At 100 rows those dominate, so tracking is a smaller *fraction* of the total and turning it off saves proportionally less. By 1,000 rows the per-entity cost has taken over and the ratio settles near 0.52.

So tracking overhead is per-entity, but that only becomes visible once there are enough entities to outweigh the fixed cost of running a query at all. A benchmark at a single row count would have missed this entirely.

### GC pressure, not just allocation volume

The Gen2 column is the practically important one. At 10,000 rows both variants trigger **Gen2 collections** — 250 and 267 per 1,000 operations. Gen2 is the full, blocking collection, and it is what turns allocation pressure into visible latency spikes rather than just memory use.

`AsNoTracking` halves the allocations but does not escape Gen2 here, because 10,000 materialised entities is a lot of objects either way. The lesson is that `AsNoTracking` reduces the pressure, it does not remove the need to think about how many rows you are pulling into memory.

### Warnings from the run

BenchmarkDotNet flagged two things, kept here rather than dropped:

```
MultimodalDistribution
  TrackingBenchmarks.Tracked -> It seems that the distribution is bimodal (mValue = 3.43)
MinIterationTime
  TrackingBenchmarks.Tracked -> The minimum observed iteration time is 95.678ms which is
                                very small. It's recommended to increase it to at least 100ms
```

The bimodal warning applies to the 10,000-row tracked case — two clusters of timings rather than one, most likely GC pauses landing in some iterations and not others. The mean is correspondingly less trustworthy there, which is a reason to lean on the allocation figures rather than the timings.

## Identity resolution

Two separate queries for the same row, same context:

```
Tracked:    same instance? True,  tracker holds 1
NoTracking: same instance? False, tracker holds 0
```

With tracking, the second query finds Id=1 already in the identity map and returns **the same object reference**. Without it there is no identity map, so a fresh object is materialised and you get two independent instances holding the same data — mutate one and the other does not see it.

This reaches further than duplicate queries. A single query with an `Include` across a many-to-many can return the same parent row many times; tracking collapses those into one object, `AsNoTracking` does not.

## When you would NOT use AsNoTracking

**One line: any time you intend to modify what you read, because without a snapshot `SaveChanges` has nothing to compare against and silently writes nothing.**

Demonstrated rather than asserted:

```
NoTracking: SaveChanges wrote 0 rows, tracker holds 0
Tracked:    tracker holds 1, state Modified
```

The untracked path is the dangerous one. `SaveChanges` returned **0** — no exception, no warning. The entity was modified in memory and the change was discarded. A bug of this shape presents as "the update didn't work" with nothing in the logs to explain it.

Two secondary cases:

- **Identity resolution is relied upon.** Code that assumes repeated appearances of an entity are one object breaks quietly under `AsNoTracking`.
- **Relationship fixup.** EF wires navigation properties between tracked entities. Untracked results do not get that, so navigations can be null where you expected them populated.

## Where this already applies in the codebase

`CollectionRepository.GetAllAsync` uses `AsNoTracking()`, added during the Day 5 N+1 fix. It is a pure read path feeding a JSON response, so the tracking overhead was pure waste. The write paths in the same repository — `AddAsync`, `UpdateAsync` — deliberately do not use it, because they need the tracker to detect what changed.