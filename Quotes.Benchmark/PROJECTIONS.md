# Day 10 — Query translation and projections

Logging the SQL EF Core generates, rewriting a whole-entity query as a projection, and catching an accidental client-side evaluation.

Demo: [`Projections.cs`](Projections.cs). Run with:

```bash
cd Quotes.Benchmark
dotnet run -c Release -- --projections
```

Table: `Quotes` with 10,000 rows — `Id, Author, Text, CreatedAt, IsDeleted`, where `Text` is up to 1000 characters.

## Turning on SQL logging

In [`QuotesApi/Extensions/InfrastructureExtensions.cs`](../QuotesApi/Extensions/InfrastructureExtensions.cs), gated on the environment:

```csharp
services.AddDbContext<QuotesDbContext>(options =>
{
    options.UseSqlite(connectionString);

    if (isDevelopment)
    {
        // Development only. EnableSensitiveDataLogging puts parameter
        // VALUES in the log, which would leak user data in production.
        options.LogTo(Console.WriteLine, LogLevel.Information)
               .EnableSensitiveDataLogging()
               .EnableDetailedErrors();
    }
});
```

`Program.cs` passes `builder.Environment.IsDevelopment()`. The gate is not decoration — `EnableSensitiveDataLogging` writes parameter values into the log, so on a production system it would put user data wherever the logs go.

## 1. Whole entity — the original

```csharp
var rows = ctx.Quotes
    .Where(q => !q.IsDeleted)
    .OrderBy(q => q.Id)
    .Take(5)
    .ToList();
```

Generated SQL:

```sql
SELECT "q"."Id", "q"."Author", "q"."CreatedAt", "q"."IsDeleted", "q"."Text"
FROM "Quotes" AS "q"
WHERE NOT ("q"."IsDeleted")
ORDER BY "q"."Id"
LIMIT @p
```

Five columns, including `Text` at up to 1000 characters. If the caller only needs an id and an author, most of that payload is transferred and materialised for nothing. EF also builds a full `Quote` entity per row, with the change-tracker snapshot that entails.

## 2. Projection — the rewrite

```csharp
var rows = ctx.Quotes
    .Where(q => !q.IsDeleted)
    .OrderBy(q => q.Id)
    .Take(5)
    .Select(q => new QuoteSummary(q.Id, q.Author, q.CreatedAt))
    .ToList();

public record QuoteSummary(int Id, string Author, DateTime CreatedAt);
```

Generated SQL:

```sql
SELECT "q"."Id", "q"."Author", "q"."CreatedAt"
FROM "Quotes" AS "q"
WHERE NOT ("q"."IsDeleted")
ORDER BY "q"."Id"
LIMIT @p
```

`Text` and `IsDeleted` are gone from the SELECT list. The `Select` is translated, not applied afterwards — EF reads the projection and narrows the SQL to exactly the columns it needs.

There are two savings here, not one. Less data crosses the wire, **and** EF materialises a small DTO rather than a tracked entity, so there is no snapshot and no identity-map entry. A projection is implicitly untracked for that reason: there is no entity to track.

## 3. The accidental client evaluation

EF Core 3.0 and later throw on most client evaluation, precisely because silently evaluating a filter in memory was causing production incidents. But `AsEnumerable()` and `ToList()` are explicit opt-outs — they end the `IQueryable`, and everything after them runs in LINQ-to-Objects with no warning at all.

**Broken:**

```csharp
var rows = ctx.Quotes
    .AsEnumerable()                                    // <-- query ends here
    .Where(q => q.Author.StartsWith("Author 1"))
    .Take(5)
    .ToList();
```

```sql
SELECT "q"."Id", "q"."Author", "q"."CreatedAt", "q"."IsDeleted", "q"."Text"
FROM "Quotes" AS "q"
```

**No `WHERE`. No `LIMIT`.** The filter and the `Take(5)` both ran on the client, after every row had already been fetched.

**Fixed:**

```csharp
var rows = ctx.Quotes
    .Where(q => q.Author.StartsWith("Author 1"))
    .Take(5)
    .Select(q => new QuoteSummary(q.Id, q.Author, q.CreatedAt))
    .ToList();
```

```sql
SELECT "q"."Id", "q"."Author", "q"."CreatedAt"
FROM "Quotes" AS "q"
WHERE "q"."Author" LIKE 'Author 1%'
LIMIT @p
```

Both predicates pushed into SQL, and the projection narrows the columns as well.

### What it actually cost

```
Table holds 10000 rows.
4440 match the filter.
The client-eval version transferred all 10000 to return 5.
```

The broken query moved **10,000 rows across the wire to return 5** — every column of every row, including the 1000-character `Text` field. That is 2,000 times more rows than the caller wanted.

Two things make this the dangerous shape of bug. It produces **correct results**, so tests pass and nobody notices. And it scales with table size rather than result size, so it is imperceptible on a development database with 20 rows and catastrophic on production with millions. The only way to see it is to look at the SQL, which is why the logging in section 1 is the point of this exercise rather than a preliminary to it.

`StartsWith` is worth noting too: EF translated it to `LIKE 'Author 1%'`, which a database can serve from an index. Had the filter been `Contains`, the translation would be `LIKE '%...%'`, which cannot use an index and scans regardless — translated to SQL, but still slow.

## Applying it to the codebase

`QuoteRepository.GetPagedAsync` currently returns whole `Quote` entities and the endpoint maps them to `QuoteResponse` afterwards, in memory. That fetches `IsDeleted` and the full `Text` for every row on the list endpoint. Projecting in the query would narrow the SQL to the columns `QuoteResponse` actually uses — a change worth making, noted here rather than done blind, since it touches a path with tests around it.