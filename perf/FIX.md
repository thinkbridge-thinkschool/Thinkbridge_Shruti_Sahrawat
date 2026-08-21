# Day 11 — Dropping p99 by 10×

Fixing the two problems found in [`README.md`](README.md), and re-measuring under identical load.

Target was ≥10×. **Achieved 241× on p99.**

## Results

k6, 5 virtual users, 60 seconds, same script both times.

| | Before | After | Factor |
|---|---|---|---|
| **p99** | **67,000 ms** | **278.05 ms** | **241×** |
| p95 | 67,000 ms | 244.10 ms | 274× |
| p50 | 67,000 ms | 176.86 ms | 379× |
| Mean | 67,000 ms | 182.20 ms | 368× |
| Max | 67,000 ms | 391.32 ms | 171× |
| Requests in 60 s | 5 | 1,640 | 328× |
| Throughput | 0.074 req/s | 27.30 req/s | 369× |

Both runs returned 100% 200s, so the improvement is not bought by dropping work.

## Change 1 — eliminate the N+1

**Before:** one query for the author list, then one query per author. 251 queries per request.

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

**After:** one query, aggregated in the database.

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

var results = rows
    .Select(r => new AuthorStats(r.Author, r.QuoteCount, r.MostRecent))
    .ToList();
```

Two details worth noting about writing it.

The projection is to an **anonymous type**, not straight to the `AuthorStats` record. EF Core could not translate a record constructor inside a `Select` over a `GroupBy` and threw `InvalidOperationException: The LINQ expression could not be translated`. The DTO is built after materialisation instead. That exception is EF Core 3.0+ refusing to fall back silently to client evaluation, which is the behaviour the previous exercise was about — it would rather fail loudly than quietly pull the table into memory.

`Take(20)` now applies **before** materialisation, so `LIMIT` reaches SQL. In the original, 20 rows were selected in memory after every author's full quote list had already been fetched.

## Change 2 — add the index

```sql
CREATE INDEX IX_Quotes_Author ON Quotes(Author);
```

The table previously had **no indexes at all** — confirmed against `sqlite_master`.

## Before and after plans

### The N+1 inner query

```
-- Before
EXPLAIN QUERY PLAN
SELECT Id, Author, CreatedAt, IsDeleted, Text
FROM Quotes WHERE Author = 'Author 42' AND NOT IsDeleted;

QUERY PLAN
`--SCAN Quotes
```

```
-- After the index
QUERY PLAN
`--SEARCH Quotes USING INDEX IX_Quotes_Author (Author=?)
```

Scan to seek: 10,000 rows read to find ~40, versus descending the index directly.

### The aggregate query

```
-- Without the index
EXPLAIN QUERY PLAN
SELECT Author, COUNT(*), MAX(CreatedAt)
FROM Quotes WHERE NOT IsDeleted
GROUP BY Author ORDER BY COUNT(*) DESC LIMIT 20;

QUERY PLAN
|--SCAN Quotes
|--USE TEMP B-TREE FOR GROUP BY
`--USE TEMP B-TREE FOR ORDER BY
```

```
-- With the index
QUERY PLAN
|--SCAN Quotes USING INDEX IX_Quotes_Author
`--USE TEMP B-TREE FOR ORDER BY
```

**The `USE TEMP B-TREE FOR GROUP BY` line is gone.** A `GROUP BY` needs rows with the same key adjacent; without an index SQLite builds a temporary B-tree to sort them into that order. An index on `Author` already stores rows in author order, so walking it produces the grouping for free.

So the index earns its place twice, in two different ways: it converts a scan into a seek on the N+1 query, and it removes a sort on the aggregate query. Those are different mechanisms.

Two things it does **not** do. It still says `SCAN`, not `SEARCH`, because the aggregate has no predicate on `Author` — the whole index is read, just a narrower structure than the whole table. And the second temp B-tree remains: the `ORDER BY` is on `COUNT(*)`, a computed aggregate, which no index on a base column can pre-sort.

The measured effect of that second job is visible in the load numbers. An earlier run of the fixed endpoint *before* the index existed gave p50 220.78 ms and 1,308 requests; with the index it is p50 176.86 ms and 1,640 requests — roughly 20% better on an already-fixed query.

## Which change mattered more

Measured separately in the previous exercise:

| | Single request | p50 under load |
|---|---|---|
| Neither fix | 14.32 s | 67.0 s |
| Index only | 11.68 s | 51.86 s |
| Both | 0.19 s | 176.86 ms |

**The index alone bought 18%. Eliminating the N+1 is what produced the order-of-magnitude change.** The index is worth having — it contributes at both ends — but a developer who added it, saw a 20% improvement and stopped would have left the endpoint unusable.

## What the percentile distribution says

Before, p50, p95 and p99 were identical at 67 s. That is saturation: every request had the same experience because they were all in one queue, and nobody was served quickly.

After, they spread — 176.86 / 244.10 / 278.05 ms, with a 391 ms max. A distribution with shape means requests are being served rather than queued, and the tail is close enough to the median (1.6× at p99) that there is no long-tail problem hiding behind a good average.