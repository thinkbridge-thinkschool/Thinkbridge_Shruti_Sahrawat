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
                ItemCount = c.Items.Count,
                MostRecentlyAdded = c.Items.Count == 0 ? (DateTime?)null : c.Items.Max(i => i.AddedAt),
                PreviewItems = c.Items
                    .OrderByDescending(i => i.AddedAt)
                    .Take(request.PreviewSize)
                    .Select(i => new { i.QuoteId, i.AddedAt })
                    .ToList()
            })
            .ToListAsync(ct);

        // One query for every quote that actually appears in a preview, not
        // every quote referenced anywhere - built from PreviewItems (already
        // trimmed server-side), so we never fetch a quote we won't return.
        // This is the N+1 lesson from Day 5 and Day 11, applied one level
        // deeper: the fix has to bound the *first* query, not just the
        // in-memory shaping after it.
        var quoteIds = collections
            .SelectMany(c => c.PreviewItems.Select(i => i.QuoteId))
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
                c.ItemCount,
                c.MostRecentlyAdded,
                c.PreviewItems
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