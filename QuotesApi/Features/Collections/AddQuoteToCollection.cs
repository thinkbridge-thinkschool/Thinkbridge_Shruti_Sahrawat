using MediatR;
using QuotesApi.Repositories;
using QuotesApi.Services;

namespace QuotesApi.Features.Collections;

public sealed record AddQuoteToCollectionCommand(int CollectionId, int QuoteId)
    : IRequest<bool>;

// Loads the aggregate, calls AddItem, saves. AddItem is where the invariants
// live - maximum 50 items, no duplicate quote IDs, positive quote ID - so the
// handler does not re-check any of them. It lets them throw.
//
// The handler is where IClock is injected and read. That is deliberate: the
// application layer owns "what time is it", the aggregate owns "is this legal".
// Keeping the clock out of the domain is what lets CollectionTests assert an
// exact AddedAt instead of a tolerance window.
//
// Returns bool for found/not-found rather than the collection. The caller
// already knows what it asked to add.
public sealed class AddQuoteToCollectionHandler(ICollectionRepository repository, IClock clock)
    : IRequestHandler<AddQuoteToCollectionCommand, bool>
{
    public async Task<bool> Handle(AddQuoteToCollectionCommand request, CancellationToken ct)
    {
        var collection = await repository.GetByIdAsync(request.CollectionId, ct);
        if (collection is null) return false;

        collection.AddItem(request.QuoteId, clock.UtcNow);
        await repository.UpdateAsync(collection, ct);
        return true;
    }
}
