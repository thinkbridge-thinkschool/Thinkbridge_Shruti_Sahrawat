# Day 11 — Profiling a slow endpoint

A deliberately slow endpoint with two independent problems, profiled under load with k6, with the SQL and execution plans that explain the numbers.

- Endpoints: [`QuotesApi/Extensions/ProfilingEndpoints.cs`](../QuotesApi/Extensions/ProfilingEndpoints.cs)
- Load script: [`load-test.js`](load-test.js)
- Data: 10,000 quotes across 250 authors, SQLite

```bash
k6 run --env TARGET=slow load-test.js
k6 run --env TARGET=fast load-test.js
```

## The endpoint

`GET /api/profiling/author-stats-slow` returns the top 20 authors by quote count with each author's most recent quote. It is written the way this often gets written by accident:

```csharp
var authors = await db.Quotes
    .Where(q => !q.IsDeleted)
    .Select(q => q.Author)
    .Distinct()
    .ToListAsync(ct);

foreach (var author in authors)
{
    var quotes = await db.Quotes
        .Where(q => q.Author == author && !q.IsDeleted)
        .ToListAsync(ct);

    results.Add(new AuthorStats(author, quotes.Count, quotes.Max(q => q.CreatedAt)));
}
```

## Baseline under load

5 virtual users, 60 seconds.

| | p50 | p95 | p99 | Requests in 60s | Throughput |
|---|---|---|---|---|---|
| **Slow (baseline)** | 67.0 s | 67.0 s | 67.0 s | 5 | 0.074 req/s |
| Slow + index on Author | 51.86 s | 52.79 s | 52.84 s | 5 | 0.056 req/s |
| **Fixed** | **220.78 ms** | **303.66 ms** | **385.06 ms** | **1,308** | **21.77 req/s** |

A single uncontended request took **14.32 s**. Under 5 concurrent users it took **67 s**, and 5 × 14 ≈ 70. With this setup the concurrent requests showed strong contention, so the 5-VU result is dominated by queueing rather than representing five independent 14-second executions. I did not instrument where the contention occurs, so this describes the observation rather than claiming a mechanism.

**The percentile shape is itself a diagnostic.** On the baseline, p50, p95 and p99 are identical to three significant figures. Percentiles collapsing onto a single value means every request had the same experience, which is what saturation looks like — nobody got served quickly because everybody was in the same queue. The fixed endpoint shows a real distribution: 220 ms median, 385 ms at p99, 562 ms max. Spread is healthy; a flat line is not.

## The offending SQL

One query for the author list, then this **250 times per request**:

```sql
SELECT "q"."Id", "q"."Author", "q"."CreatedAt", "q"."IsDeleted", "q"."Text"
FROM "Quotes" AS "q"
WHERE "q"."Author" = @author AND NOT ("q"."IsDeleted")
```

Two things wrong with it beyond the count. It selects every column including `Text` (up to 1000 characters) when only `CreatedAt` is used, and it filters on `Author`, which had no index.

Each execution logged 5–7 ms. 251 × 6 ms ≈ 1.5 s of logged database time against a 14 s request, so roughly 90% of the wall clock is not accounted for by query execution. I did not profile what the remainder consists of — plausible contributors are EF materialising and change-tracking the entities the loop pulls back across all iterations, per-query command setup, and the LINQ `Max` over each returned list. Naming a single cause without measuring it would be a guess.

## The execution plan

```
EXPLAIN QUERY PLAN
SELECT Id, Author, CreatedAt, IsDeleted, Text
FROM Quotes WHERE Author = 'Author 42' AND NOT IsDeleted;

QUERY PLAN
`--SCAN Quotes
```

`SCAN` — a full table scan, 10,000 rows, to find roughly 40. Checking the table confirmed why:

```sql
SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='Quotes';
-- (no rows)
```

No indexes at all. After adding one:

```sql
CREATE INDEX IX_Quotes_Author ON Quotes(Author);

QUERY PLAN
`--SEARCH Quotes USING INDEX IX_Quotes_Author (Author=?)
```

`SCAN` became `SEARCH ... USING INDEX`. Scan to seek.

## The two biggest problems

### 1. The N+1 — 251 queries where 1 would do

This is the dominant cost. The loop issues one query per author, so the work scales with the number of authors rather than the size of the answer. The endpoint returns 20 rows and executes 251 queries to produce them.

The fix is to let the database do the aggregation:

```csharp
var rows = await db.Quotes
    .Where(q => !q.IsDeleted)
    .GroupBy(q => q.Author)
    .Select(g => new
    {
        Author = g.Key,
        QuoteCount = g.Count(),
        MostRecent = g.Max(q => q.CreatedAt)
    })
    .OrderByDescending(r => r.QuoteCount)
    .Take(20)
    .ToListAsync(ct);
```

```sql
SELECT "q"."Author", COUNT(*) AS "QuoteCount", MAX("q"."CreatedAt") AS "MostRecent"
FROM "Quotes" AS "q"
WHERE NOT ("q"."IsDeleted")
GROUP BY "q"."Author"
ORDER BY COUNT(*) DESC
LIMIT @p
```

One statement, aggregated in the database, projected to three columns rather than five, and `LIMIT` applied server-side.

A note on writing it: EF could not translate a record constructor inside the `Select` over a `GroupBy` — it threw `InvalidOperationException: The LINQ expression could not be translated`. Projecting to an anonymous type and building the DTO after materialisation works. That is EF Core 3.0+ refusing to silently fall back to client evaluation, which is the behaviour the previous exercise was about.

### 2. The missing index on `Author`

Every one of the 250 inner queries scanned the whole table. `CREATE INDEX IX_Quotes_Author ON Quotes(Author)` turns each of them into a seek.

**But this is the smaller problem, and that is the useful finding.** Adding the index alone moved the single request from 14.32 s to 11.68 s — an 18% improvement — and p50 under load from 67 s to 51.9 s. Real, and nowhere near enough. The endpoint was still 235× slower than the fixed version.

The two problems are independent. The index makes each query faster; it does not make 251 queries stop being 251 queries. Someone who adds an index, sees a 20% improvement and stops has fixed the smaller half.

## A note on the measurement

Five virtual users is a deliberately low number. The slow endpoint takes ~14 s per request, so higher concurrency would mostly measure queue depth. The baseline run completed only 5 requests in 60 seconds; the index run completed 5 and left 5 more interrupted at the cutoff. Those are small samples, and the slow-path percentiles should be read as indicative rather than precise — though the effect size is large enough that precision is not the constraint.

Running against SQLite also means these absolute numbers are specific to an embedded database on a laptop, with no network between the application and the data. Against a networked SQL Server each of the 251 queries would additionally pay network latency, so the N+1 penalty would very likely be worse — though that is reasoning from the shape of the problem rather than something measured here.