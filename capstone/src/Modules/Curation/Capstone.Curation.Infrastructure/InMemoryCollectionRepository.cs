using Capstone.Curation.Application.Abstractions;
using Capstone.Curation.Domain;

namespace Capstone.Curation.Infrastructure;

/// <summary>
/// Scaffold repository. EF Core mapping is the next piece of work, and the
/// shape it has to take is already decided: Collection as the entity, Items as
/// an owned collection, CollectionId and CuratorId through value converters.
/// </summary>
public sealed class InMemoryCollectionRepository(UnitOfWork unitOfWork) : ICollectionRepository
{
    private readonly Dictionary<CollectionId, Collection> _collections = [];

    public Task<Collection?> FindAsync(CollectionId id, CancellationToken cancellationToken)
    {
        _collections.TryGetValue(id, out var collection);

        if (collection is not null)
        {
            // Standing in for the change tracker: whatever a repository hands
            // out is what the unit of work is responsible for committing.
            unitOfWork.Track(collection);
        }

        return Task.FromResult(collection);
    }

    public void Add(Collection collection)
    {
        _collections[collection.Id] = collection;
        unitOfWork.Track(collection);
    }
}
