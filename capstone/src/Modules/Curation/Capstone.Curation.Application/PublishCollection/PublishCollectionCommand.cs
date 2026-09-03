using Capstone.Catalog.Contracts;
using Capstone.Curation.Application.Abstractions;
using Capstone.Curation.Domain;
using Capstone.SharedKernel;

namespace Capstone.Curation.Application.PublishCollection;

public sealed record PublishCollectionCommand(Guid CollectionId, string CuratorId);

/// <summary>
/// The slice: a curator publishes a collection, and their followers eventually
/// see it.
/// </summary>
/// <remarks>
/// Everything this handler does that the aggregate could not do for itself is
/// something requiring data the aggregate cannot see:
///
/// <list type="bullet">
/// <item><b>Authorisation.</b> Whether this curator owns this collection is a
/// comparison the aggregate could technically make, but the decision to answer
/// "not yours" as if it did not exist is an application-level policy about
/// information disclosure, not a domain rule.</item>
/// <item><b>Quote existence.</b> Only Catalog knows, and asking it is I/O. An
/// aggregate that performed I/O inside an invariant would be untestable
/// without a network.</item>
/// <item><b>The clock.</b> Owned here and passed in, so the domain stays
/// deterministic - Day 2's rule.</item>
/// </list>
///
/// What it deliberately does not do is publish a message. It changes state and
/// commits; the outbox carries the announcement. See <see cref="IUnitOfWork"/>.
/// </remarks>
public sealed class PublishCollectionHandler(
    ICollectionRepository collections,
    IQuoteCatalog catalog,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task HandleAsync(PublishCollectionCommand command, CancellationToken cancellationToken)
    {
        var id = new CollectionId(command.CollectionId);
        var curator = new CuratorId(command.CuratorId);

        var collection = await collections.FindAsync(id, cancellationToken)
            ?? throw new DomainException($"Collection {id} was not found.");

        if (collection.CuratorId != curator)
        {
            // Same message as "not found", on purpose: a different message here
            // would let anyone enumerate which collection ids exist by reading
            // the error text.
            throw new DomainException($"Collection {id} was not found.");
        }

        var quoteIds = collection.Items.Select(item => item.QuoteId.Value).ToArray();
        var missing = await catalog.FindMissingAsync(quoteIds, cancellationToken);

        if (missing.Count > 0)
        {
            throw new DomainException(
                $"Cannot publish: quote(s) {string.Join(", ", missing)} no longer exist.");
        }

        collection.Publish(clock.GetUtcNow());

        await unitOfWork.CommitAsync(cancellationToken);
    }
}
