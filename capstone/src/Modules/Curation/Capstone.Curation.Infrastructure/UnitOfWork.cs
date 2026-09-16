using Capstone.Curation.Application.Abstractions;
using Capstone.Curation.Domain;
using Capstone.Curation.Infrastructure.Outbox;

namespace Capstone.Curation.Infrastructure;

/// <summary>
/// Commits tracked aggregates and the outbox rows their events produced, as
/// one unit.
/// </summary>
/// <remarks>
/// As of day 2 of the build plan this is finally true rather than aspirational.
/// The sequence has not changed since the scaffold -
///
/// <list type="number">
/// <item>drain every tracked aggregate's domain events;</item>
/// <item>translate each into an outbox record;</item>
/// <item>clear the aggregate's events so a second commit cannot republish
/// them;</item>
/// <item>persist state and outbox rows in a single transaction.</item>
/// </list>
///
/// - but step 4 only became real when the outbox became a table in the same
/// DbContext. Yesterday <c>outbox.Enqueue</c> pushed onto an in-memory queue
/// that was not part of any transaction, so a <c>SaveChangesAsync</c> that
/// threw left an announcement staged for a state change that had not
/// happened. Today <c>Enqueue</c> adds a row to the same change tracker, and
/// the single save below either writes the collection and its outbox row or
/// writes neither. The ordering of the two lines stopped mattering, which is
/// the sign the gap actually closed rather than moved.
///
/// No separate Track() list - the DbContext's own change tracker is the list.
/// Everything CollectionRepository loaded or added in this request is already
/// in it, because both operations went through the same scoped
/// CurationDbContext instance.
/// </remarks>
public sealed class UnitOfWork(CurationDbContext db, IOutboxStore outbox) : IUnitOfWork
{
    public async Task<int> CommitAsync(CancellationToken cancellationToken)
    {
        var staged = 0;

        foreach (var entry in db.ChangeTracker.Entries<Collection>().ToList())
        {
            var aggregate = entry.Entity;

            foreach (var domainEvent in aggregate.DomainEvents)
            {
                var record = DomainEventTranslator.ToOutboxRecord(domainEvent);

                if (record is not null)
                {
                    outbox.Enqueue(record);
                    staged++;
                }
            }

            aggregate.ClearDomainEvents();
        }

        await db.SaveChangesAsync(cancellationToken);

        return staged;
    }
}
