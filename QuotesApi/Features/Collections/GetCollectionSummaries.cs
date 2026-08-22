using MediatR;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;

namespace QuotesApi.Features.Collections;

public sealed record GetCollectionSummariesQuery(string? OwnerId = null, int PreviewSize = 3)
    : IRequest<IReadOnlyList<CollectionSummary>>;

// Query handler. Goes to the DbContext directly - no repository, no aggregate.
//
// The read path has no business enforcing invariants, so it has no reason to
// load an entity capable of enforcing them. AsNoTracking because nothing here
// will be modified, and a projection so the SQL fetches only what the screen
// displays.
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
                c.Id,
                c.Name,
                c.OwnerId,
                Items = c.Items
                    .OrderByDescending(i => i.AddedAt)
                    .Select(i => new { i.QuoteId, i.AddedAt })
                    .ToList()
            })
            .ToListAsync(ct);

        // One query for every quote referenced across all collections, rather
        // than one per collection - the N+1 lesson from Day 5 and Day 11.
        var quoteIds = collections
            .SelectMany(c => c.Items.Select(i => i.QuoteId))
            .Distinct()
            .ToList();

        var quotes = await db.Quotes
            .AsNoTracking()
            .Where(q => quoteIds.Contains(q.Id))
            .Select(q => new { q.Id, q.Author, q.Text })
            .ToDictionaryAsync(q => q.Id, ct);

        return collections
            .Select(c => new CollectionSummary(
                c.Id,
                c.Name,
                c.OwnerId,
                c.Items.Count,
                c.Items.Count == 0 ? null : c.Items.Max(i => i.AddedAt),
                c.Items
                    .Take(request.PreviewSize)
                    .Where(i => quotes.ContainsKey(i.QuoteId))
                    .Select(i => new CollectionPreviewItem(
                        i.QuoteId,
                        quotes[i.QuoteId].Author,
                        quotes[i.QuoteId].Text,
                        i.AddedAt))
                    .ToList()))
            .ToList();
    }
}