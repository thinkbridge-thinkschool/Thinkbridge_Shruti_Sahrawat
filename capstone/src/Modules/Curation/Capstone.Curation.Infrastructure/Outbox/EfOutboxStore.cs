using Microsoft.EntityFrameworkCore;

namespace Capstone.Curation.Infrastructure.Outbox;

/// <summary>
/// The outbox as a table in the same database as the aggregate.
/// </summary>
/// <remarks>
/// Same database is the point, not a convenience. The entire argument for an
/// outbox is that the state change and the record of it commit together or not
/// at all, and that is only true while one transaction can span both. Put the
/// outbox in a different store - a queue, another database, a broker - and
/// the gap between "collection published" and "publish announced" reopens,
/// which is the gap Day 20 established and this day finally closes here.
///
/// <see cref="Enqueue"/> therefore does not save. It adds to the change
/// tracker and leaves the commit to <see cref="UnitOfWork"/>, whose single
/// <c>SaveChangesAsync</c> writes the collection, its items and this row in
/// one transaction. Yesterday's version of this class enqueued into a
/// separate in-memory queue before that save, which meant a failed commit
/// left an announcement staged for a state change that never happened - the
/// exact failure this comment now exists to say cannot occur.
/// </remarks>
public sealed class EfOutboxStore(CurationDbContext db, TimeProvider clock) : IOutboxStore
{
    public void Enqueue(OutboxRecord record)
        => db.OutboxMessages.Add(OutboxMessage.Stage(record));

    public async Task<IReadOnlyList<OutboxRecord>> ReadUnsentAsync(
        int maxCount, CancellationToken cancellationToken)
        => await db.OutboxMessages
            .Where(message => message.SentAt == null)
            // Oldest first, so a backlog drains in the order it accumulated.
            // Subscribers must not depend on this - a broker gives no such
            // guarantee once there is more than one consumer or a retry in
            // flight - but there is no reason to deliver out of order here.
            .OrderBy(message => message.OccurredAt)
            .Take(maxCount)
            // Projected to the record rather than returned as entities. The
            // relay has no business holding a tracked OutboxMessage: it would
            // then be able to mutate SentAt directly and bypass the
            // acknowledge step that is the whole design.
            .Select(message => new OutboxRecord(
                message.MessageId, message.EventType, message.Payload, message.OccurredAt))
            .ToListAsync(cancellationToken);

    public async Task MarkSentAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var message = await db.OutboxMessages
            .FirstOrDefaultAsync(candidate => candidate.MessageId == messageId, cancellationToken);

        // Silent on a miss, deliberately. A relay that crashed between
        // delivering and acknowledging comes back and tries to acknowledge a
        // row it may already have stamped. Treating that as an error would
        // turn correct recovery into a logged failure.
        if (message is null || message.SentAt is not null)
        {
            return;
        }

        message.MarkSent(clock.GetUtcNow());

        await db.SaveChangesAsync(cancellationToken);
    }
}
