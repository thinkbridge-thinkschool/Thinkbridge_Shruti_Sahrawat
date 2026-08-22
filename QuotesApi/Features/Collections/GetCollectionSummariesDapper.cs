using System.Data;
using Dapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;

namespace QuotesApi.Features.Collections;

public sealed record GetCollectionSummariesDapperQuery(string? OwnerId = null, int PreviewSize = 3)
    : IRequest<IReadOnlyList<CollectionSummary>>;

// Dapper counterpart to GetCollectionSummariesHandler (GetCollectionSummaries.cs).
// Same contract, same two-query shape, same CollectionSummary/CollectionPreviewItem
// output - hand-written SQL instead of a LINQ projection, for comparison.
//
// Reuses the EF-managed connection (db.Database.GetDbConnection()) rather than
// opening a second one, so this handler shares the same pooled SQLite connection
// as the EF path instead of standing up its own.
public sealed class GetCollectionSummariesDapperHandler(QuotesDbContext db)
    : IRequestHandler<GetCollectionSummariesDapperQuery, IReadOnlyList<CollectionSummary>>
{
    // ItemCount and MostRecentlyAdded come from an unfiltered GROUP BY over the
    // full item set - the same "count/max over everything" job the correlated
    // COUNT/MAX subqueries do in the EF-generated SQL. The preview join is the
    // only place ROW_NUMBER() caps rows, partitioned per collection and ordered
    // newest-first, matching EF's window over CollectionItem.
    private const string CollectionsSql = """
        SELECT c."Id"      AS Id,
               c."Name"    AS Name,
               c."OwnerId" AS OwnerId,
               COALESCE(agg."ItemCount", 0) AS ItemCount,
               agg."MostRecentlyAdded"      AS MostRecentlyAdded,
               p."QuoteId" AS PreviewQuoteId,
               p."AddedAt" AS PreviewAddedAt
        FROM "Collections" AS c
        LEFT JOIN (
            SELECT "CollectionId",
                   COUNT(*)       AS "ItemCount",
                   MAX("AddedAt") AS "MostRecentlyAdded"
            FROM "CollectionItem"
            GROUP BY "CollectionId"
        ) AS agg ON agg."CollectionId" = c."Id"
        LEFT JOIN (
            SELECT "CollectionId", "QuoteId", "AddedAt"
            FROM (
                SELECT "CollectionId", "QuoteId", "AddedAt",
                       ROW_NUMBER() OVER (
                           PARTITION BY "CollectionId" ORDER BY "AddedAt" DESC
                       ) AS "Row"
                FROM "CollectionItem"
            )
            WHERE "Row" <= @PreviewSize
        ) AS p ON p."CollectionId" = c."Id"
        WHERE (@OwnerId IS NULL OR c."OwnerId" = @OwnerId)
        ORDER BY c."Id", p."AddedAt" DESC
        """;

    // Same N+1 guard as the EF handler: only the quote ids that actually made it
    // into a preview, built from the first query's already-trimmed rows.
    private const string QuotesSql = """
        SELECT "Id" AS Id, "Author" AS Author, "Text" AS Text
        FROM "Quotes"
        WHERE "Id" IN @QuoteIds
        """;

    private sealed class CollectionRow
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public string OwnerId { get; init; } = "";
        public int ItemCount { get; init; }
        public DateTime? MostRecentlyAdded { get; init; }
        public int? PreviewQuoteId { get; init; }
        public DateTime? PreviewAddedAt { get; init; }
    }

    private sealed class QuoteRow
    {
        public int Id { get; init; }
        public string Author { get; init; } = "";
        public string Text { get; init; } = "";
    }

    public async Task<IReadOnlyList<CollectionSummary>> Handle(
        GetCollectionSummariesDapperQuery request, CancellationToken ct)
    {
        IDbConnection connection = db.Database.GetDbConnection();

        var rows = (await connection.QueryAsync<CollectionRow>(new CommandDefinition(
            CollectionsSql,
            new { request.OwnerId, request.PreviewSize },
            cancellationToken: ct))).ToList();

        var quoteIds = rows
            .Where(r => r.PreviewQuoteId is not null)
            .Select(r => r.PreviewQuoteId!.Value)
            .Distinct()
            .ToList();

        var quotes = quoteIds.Count == 0
            ? new Dictionary<int, QuoteRow>()
            : (await connection.QueryAsync<QuoteRow>(new CommandDefinition(
                QuotesSql,
                new { QuoteIds = quoteIds },
                cancellationToken: ct)))
                .ToDictionary(q => q.Id);

        return rows
            .GroupBy(r => r.Id)
            .Select(g =>
            {
                var first = g.First();

                var preview = g
                    .Where(r => r.PreviewQuoteId is not null && quotes.ContainsKey(r.PreviewQuoteId.Value))
                    .Select(r => new CollectionPreviewItem(
                        r.PreviewQuoteId!.Value,
                        quotes[r.PreviewQuoteId.Value].Author,
                        quotes[r.PreviewQuoteId.Value].Text,
                        r.PreviewAddedAt!.Value))
                    .ToList();

                return new CollectionSummary(
                    first.Id,
                    first.Name,
                    first.OwnerId,
                    first.ItemCount,
                    first.MostRecentlyAdded,
                    preview);
            })
            .ToList();
    }
}
