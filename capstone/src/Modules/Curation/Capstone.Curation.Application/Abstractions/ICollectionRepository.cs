using Capstone.Curation.Domain;

namespace Capstone.Curation.Application.Abstractions;

/// <summary>
/// Loads and saves whole aggregates, and nothing finer.
/// </summary>
/// <remarks>
/// One repository per aggregate root, returning the root itself - not items,
/// not projections, not IQueryable. Handing back IQueryable would let a caller
/// compose a query that loads half an aggregate, and half an aggregate cannot
/// enforce an invariant that spans all of it.
///
/// Reads that feed screens do not come through here. They are a separate
/// concern with a separate shape, and the read-side split Day 12 built with
/// Dapper is the model: this interface exists for the write path, where the
/// consistency boundary has to hold.
/// </remarks>
public interface ICollectionRepository
{
    Task<Collection?> FindAsync(CollectionId id, CancellationToken cancellationToken);

    void Add(Collection collection);
}
