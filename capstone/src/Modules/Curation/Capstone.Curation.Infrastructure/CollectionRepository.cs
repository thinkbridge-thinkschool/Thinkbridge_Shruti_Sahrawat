using Capstone.Curation.Application.Abstractions;
using Capstone.Curation.Domain;
using Microsoft.EntityFrameworkCore;

namespace Capstone.Curation.Infrastructure;

/// <summary>
/// EF Core-backed repository. Replaces the in-memory scaffold - Day 1 of the
/// build plan in Days/day-28/README.md.
/// </summary>
/// <remarks>
/// No explicit "track" step, unlike the scaffold this replaces: a Collection
/// returned by FindAsync is already tracked by the DbContext that loaded it,
/// and Add attaches a new one to the same context. UnitOfWork.CommitAsync
/// reads whatever the context is tracking straight from its own change
/// tracker instead of a separate list - one fewer thing that can drift out of
/// sync with what EF itself believes is pending.
/// </remarks>
public sealed class CollectionRepository(CurationDbContext db) : ICollectionRepository
{
    public Task<Collection?> FindAsync(CollectionId id, CancellationToken cancellationToken)
        => db.Collections
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public void Add(Collection collection) => db.Collections.Add(collection);
}
