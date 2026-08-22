using MediatR;
using QuotesApi.Repositories;

namespace QuotesApi.Features.Collections;

public sealed record AddQuoteToCollectionCommand(int CollectionId, int QuoteId)
    : IRequest<bool>;

// Loads the aggregate, calls AddItem, saves. AddItem is where the invariants
// live - maximum 50 items, no duplicate quote IDs, positive quote ID - so the
// handler does not re-check any of them. It lets them throw.
//
// Returns bool for found/not-found rather than the collection. The caller
// already knows what it asked to add.
public sealed class AddQuoteToCollectionHandler(ICollectionRepository repository)
    : IRequestHandler<AddQuoteToCollectionCommand, bool>
{
    public async Task<bool> Handle(AddQuoteToCollectionCommand request, CancellationToken ct)
    {
        var collection = await repository.GetByIdAsync(request.CollectionId, ct);
        if (collection is null) return false;

        collection.AddItem(request.QuoteId);
        await repository.UpdateAsync(collection, ct);
        return true;
    }
}