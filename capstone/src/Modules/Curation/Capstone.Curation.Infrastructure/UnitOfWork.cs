using Capstone.Curation.Application.Abstractions;
using Capstone.Curation.Domain;
using Capstone.Curation.Infrastructure.Outbox;

namespace Capstone.Curation.Infrastructure;

/// <summary>
/// Commits tracked aggregates and the outbox rows their events produced, as one
/// unit.
/// </summary>
/// <remarks>
/// Day 1 of the build plan: the aggregate side of this is now real -
/// SaveChangesAsync against SQL rather than a dictionary assignment. The
/// outbox side stays in-memory until Day 2 builds the real table, so "one
/// transaction" is not fully true yet for the outbox row specifically - it is
/// true for the aggregate's own state, which is what this day set out to
/// prove. Making the outbox itself transactional is deliberately left for
/// Day 2 rather than folded in here, so each day lands with the tests green
/// and one clear thing changed.
///
/// No separate Track() list any more - the DbContext's own change tracker is
/// the list. Everything CollectionRepository loaded or added in this request
/// is already in it, because both operations went through the same scoped
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

        // Named honestly rather than hidden: outbox.Enqueue above already ran
        // before this line, so if SaveChangesAsync throws - a constraint
        // violation, a dropped connection - the in-memory outbox has staged
        // an event for a state change that did not actually persist. That is
        // exactly the gap a real outbox closes by writing the row in the same
        // transaction as the state change, which is Day 2's job, not this
        // day's. Today's scope is proving the aggregate's own state is real
        // SQL; the outbox becoming equally real is the very next commit.
        await db.SaveChangesAsync(cancellationToken);

        return staged;
    }
}
