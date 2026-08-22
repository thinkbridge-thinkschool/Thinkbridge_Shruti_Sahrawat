using MediatR;
using QuotesApi.Domain;
using QuotesApi.Repositories;

namespace QuotesApi.Features.Collections;

public sealed record CreateCollectionCommand(string Name, string OwnerId)
    : IRequest<int>;

// Command handler. Goes through the repository and the aggregate, because the
// aggregate is what enforces the invariants - name 3-80 characters, and every
// later mutation guarded by AddItem/RemoveItem.
//
// Returns the new id, not the entity. The write side confirms that the write
// happened; it does not owe the caller a view of the data. Anything wanting to
// display the collection asks the read side for the shape it needs.
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