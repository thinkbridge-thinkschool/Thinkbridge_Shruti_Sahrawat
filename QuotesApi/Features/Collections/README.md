# Day 12 — Read models and CQRS-lite

Splitting collections into a write path that goes through the aggregate and a read path that projects straight from the database. No event sourcing — just separate command and query types, handled by MediatR.

## Why this feature

`Collection` has real invariants: name 3–80 characters, maximum 50 items, no duplicate quote IDs. Those need the aggregate. But `CollectionItem` stores only `{ QuoteId, AddedAt }` — a foreign key — and a screen listing collections needs to show the quotes themselves, which live in a different table. The write side needs an object that enforces rules; the read side needs a join the aggregate never performs.

Before this change every endpoint returned the aggregate directly:

```json
{"id":1,"name":"Collection 1","ownerId":"shruti",
 "items":[{"quoteId":1,"addedAt":"2026-08-13T15:50:48"},
          {"quoteId":2,"addedAt":"2026-08-13T15:50:49"},
          {"quoteId":3,"addedAt":"2026-08-13T15:50:49"}]}
```

A client rendering that needs three more HTTP calls to find out what quote 1, 2 and 3 actually say.

## The command handler

[`CreateCollection.cs`](CreateCollection.cs):

```csharp
public sealed record CreateCollectionCommand(string Name, string OwnerId)
    : IRequest<int>;

public sealed class CreateCollectionHandler(ICollectionRepository repository)
    : IRequestHandler<CreateCollectionCommand, int>
{
    public async Task<int> Handle(CreateCollectionCommand request, CancellationToken ct)
    {
        var collection = new Collection(request.Name, request.OwnerId);
        await repository.AddAsync(collection, ct);
        return collection.Id;
    }
}
```

[`AddQuoteToCollection.cs`](AddQuoteToCollection.cs) is the same shape:

```csharp
public sealed class AddQuoteToCollectionHandler(ICollectionRepository repository)
    : IRequestHandler<AddQuoteToCollectionCommand, bool>
{
    public async Task<bool> Handle(AddQuoteToCollectionCommand request, CancellationToken ct)
    {
        var collection = await repository.GetByIdAsync(request.CollectionId, ct);
        if (collection is null) return false;

        collection.AddItem(request.QuoteId);   // invariants live here
        await repository.UpdateAsync(collection, ct);
        return true;
    }
}
```

Two things about these. They go **through the repository and the aggregate**, because that is where the invariants are enforced — the handler does not re-check the 50-item limit or the duplicate rule, it lets `AddItem` throw. And they return **an id or a bool, not the entity**. The write side confirms that the write happened; it does not owe the caller a view of the data.

## The query and read model

[`CollectionSummary.cs`](CollectionSummary.cs) — shaped for the screen, not the database:

```csharp
public sealed record CollectionSummary(
    int Id,
    string Name,
    string OwnerId,
    int ItemCount,
    DateTime? MostRecentlyAdded,
    IReadOnlyList<CollectionPreviewItem> Preview);

public sealed record CollectionPreviewItem(
    int QuoteId, string Author, string Text, DateTime AddedAt);
```

[`GetCollectionSummaries.cs`](GetCollectionSummaries.cs) — the handler takes `QuotesDbContext` directly. No repository, no aggregate:

```csharp
public sealed class GetCollectionSummariesHandler(QuotesDbContext db)
    : IRequestHandler<GetCollectionSummariesQuery, IReadOnlyList<CollectionSummary>>
{
    public async Task<IReadOnlyList<CollectionSummary>> Handle(
        GetCollectionSummariesQuery request, CancellationToken ct)
    {
        var collections = await db.Collections
            .AsNoTracking()
            .Where(c => request.OwnerId == null || c.OwnerId == request.OwnerId)
            .Select(c => new
            {
                c.Id, c.Name, c.OwnerId,
                ItemCount = c.Items.Count,
                MostRecentlyAdded = c.Items.Count == 0 ? (DateTime?)null : c.Items.Max(i => i.AddedAt),
                PreviewItems = c.Items
                    .OrderByDescending(i => i.AddedAt)
                    .Take(request.PreviewSize)
                    .Select(i => new { i.QuoteId, i.AddedAt })
                    .ToList()
            })
            .ToListAsync(ct);

        var quoteIds = collections.SelectMany(c => c.PreviewItems.Select(i => i.QuoteId))
                                  .Distinct().ToList();

        var quotes = await db.Quotes
            .AsNoTracking()
            .Where(q => quoteIds.Contains(q.Id))
            .Select(q => new { q.Id, q.Author, q.Text })
            .ToDictionaryAsync(q => q.Id, ct);

        // ... assemble CollectionSummary from the two results
    }
}
```

The output:

```json
[{"id":1,"name":"Collection 1","ownerId":"shruti","itemCount":3,
  "mostRecentlyAdded":"2026-08-13T15:50:49.9241761",
  "preview":[{"quoteId":3,"author":"Grace Hopper",
              "text":"The most dangerous phrase is we have always done it this way.",
              "addedAt":"2026-08-13T15:50:49.9241761"}, ...]}]
```

Author and text inline, `itemCount` and `mostRecentlyAdded` precomputed. One call, ready to render.

## The SQL it produces

Two queries for any number of collections:

```sql
SELECT "c"."Id", "c"."Name", "c"."OwnerId", (
    SELECT COUNT(*) FROM "CollectionItem" AS "c0"
    WHERE "c"."Id" = "c0"."CollectionId"), CASE
    WHEN (SELECT COUNT(*) FROM "CollectionItem" AS "c1"
          WHERE "c"."Id" = "c1"."CollectionId") = 0 THEN NULL
    ELSE (SELECT MAX("c2"."AddedAt") FROM "CollectionItem" AS "c2"
          WHERE "c"."Id" = "c2"."CollectionId")
END, "c5"."QuoteId", "c5"."AddedAt", "c5"."Id"
FROM "Collections" AS "c"
LEFT JOIN (
    SELECT "c4"."QuoteId", "c4"."AddedAt", "c4"."Id", "c4"."CollectionId"
    FROM (
        SELECT "c3"."QuoteId", "c3"."AddedAt", "c3"."Id", "c3"."CollectionId",
               ROW_NUMBER() OVER(PARTITION BY "c3"."CollectionId" ORDER BY "c3"."AddedAt" DESC) AS "row"
        FROM "CollectionItem" AS "c3"
    ) AS "c4"
    WHERE "c4"."row" <= @request_PreviewSize
) AS "c5" ON "c"."Id" = "c5"."CollectionId"
WHERE "c"."OwnerId" = @request_OwnerId
ORDER BY "c"."Id", "c5"."CollectionId", "c5"."AddedAt" DESC
```

```sql
SELECT "q"."Id", "q"."Author", "q"."Text"
FROM "Quotes" AS "q"
WHERE "q"."Id" IN (3, 2, 1)
```

The preview projection became a `LEFT JOIN` over a `ROW_NUMBER()` window rather than a query per collection, and `PreviewSize` is applied *inside* that window — the database returns at most N items per collection instead of returning all of them and trimming in memory afterwards. The `COUNT` and `MAX` subqueries carry no row filter, so `itemCount` and `mostRecentlyAdded` still reflect the whole collection, not just the preview. Quotes are then fetched in one `IN`, and only for items that actually appear in a preview. That is the N+1 lesson from Day 5 and Day 11 applied on purpose rather than found under a profiler.

Both queries select only the columns the read model exposes — `Text` is fetched because the preview displays it, `IsDeleted` is not fetched at all.

## What got simpler by separating them

**One line: the write path stopped returning domain objects over the wire, and the read path stopped loading objects it only partially uses.**

Concretely, four things:

The **aggregate is no longer a serialisation format**. It was being returned from four endpoints, which meant its internal shape was a public API contract — adding a field to `CollectionItem` would have changed what every client received. Now `CollectionSummary` is the contract and the aggregate is free to change.

The **read path cannot accidentally mutate**. It has a `DbContext` with `AsNoTracking` projections, and no path to `AddItem` or `SaveChanges`. That is not a convention; the objects it holds are records with no behaviour.

The **command handlers got shorter**. `CreateCollectionHandler` is four lines because it does exactly one thing. The old controller action mixed model construction, persistence, exception translation and response shaping in one method.

The **screen's needs stopped leaking into the domain**. `MostRecentlyAdded` and `ItemCount` are things a UI wants. Previously the only way to provide them would have been to add them to `Collection`, putting presentation concerns inside the aggregate.

## What was deliberately left alone

`GetAll`, `GetById` and `RemoveQuote` still use the repository directly. The exercise splits one feature, and leaving the others makes the difference legible in a single file rather than hiding it behind a uniform style.

That is also the honest state of a real CQRS-lite migration — it happens per feature, and a codebase mid-migration has both.