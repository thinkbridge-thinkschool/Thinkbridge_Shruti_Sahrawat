using Capstone.Curation.Application.Abstractions;
using Capstone.Curation.Domain;
using Capstone.Curation.Infrastructure.Outbox;

namespace Capstone.Curation.Infrastructure;

/// <summary>
/// Commits tracked aggregates and the outbox rows their events produced, as one
/// unit.
/// </summary>
/// <remarks>
/// Scaffold: the tracking and the commit are in-memory. The sequence, however,
/// is the real one and is the part worth pinning down early -
///
/// <list type="number">
/// <item>drain every tracked aggregate's domain events;</item>
/// <item>translate each into an outbox record;</item>
/// <item>clear the aggregate's events so a second commit cannot republish
/// them;</item>
/// <item>persist state and outbox rows in a single transaction.</item>
/// </list>
///
/// Step 4 is the whole reason this type exists rather than a handler calling a
/// publisher. Day 20 established why: a publish that happens after the commit
/// can be lost, and a publish that happens before it can announce something
/// that then rolls back. Only a row written inside the transaction is safe, and
/// only something that owns the transaction can write it.
/// </remarks>
public sealed class UnitOfWork(IOutboxStore outbox) : IUnitOfWork
{
    private readonly List<Collection> _tracked = [];

    public void Track(Collection collection)
    {
        // Reference equality is the right check: two loads of the same row
        // must not produce two tracked instances, and if they somehow did,
        // draining both would stage the same event twice.
        if (!_tracked.Contains(collection))
        {
            _tracked.Add(collection);
        }
    }

    public Task<int> CommitAsync(CancellationToken cancellationToken)
    {
        var staged = 0;

        foreach (var aggregate in _tracked)
        {
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

        // A committed aggregate is no longer this unit of work's business.
        // Holding them would both leak and, worse, risk a later commit
        // re-draining an aggregate whose events were already staged.
        _tracked.Clear();

        return Task.FromResult(staged);
    }
}
