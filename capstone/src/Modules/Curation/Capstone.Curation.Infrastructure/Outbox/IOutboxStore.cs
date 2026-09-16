namespace Capstone.Curation.Infrastructure.Outbox;

/// <summary>
/// Where integration events are staged so they commit with the state change
/// that produced them, and are acknowledged only once a subscriber has taken
/// them.
/// </summary>
/// <remarks>
/// Day 2 of the build plan grew the acknowledge step. The port used to be
/// write-only - <see cref="Enqueue"/> and nothing else - which is why the
/// scaffold implementation could get away with a destructive drain and why
/// the design review found a lost message had no record anywhere.
///
/// The three operations are deliberately separate, in this order:
///
/// <list type="number">
/// <item><see cref="Enqueue"/> stages a row in the caller's transaction. It
/// does not save - the unit of work does, which is what puts the state change
/// and the announcement in one commit.</item>
/// <item><see cref="ReadUnsentAsync"/> is what the relay polls. Oldest first,
/// bounded, and unsent only.</item>
/// <item><see cref="MarkSentAsync"/> is the acknowledgement, and it runs
/// <i>after</i> the subscriber has accepted the message rather than before.
/// Reversed, this would be at-most-once with extra steps.</item>
/// </list>
///
/// The gap this leaves, named rather than hidden: a subscriber that accepts a
/// message and then a MarkSent that fails produces a redelivery. That is
/// at-least-once behaving exactly as advertised, and it is why Sharing's
/// deduplication on (MessageId, consumer) is load-bearing rather than
/// decoration.
/// </remarks>
public interface IOutboxStore
{
    /// <summary>
    /// Stages a record in the current transaction. Does not persist on its own.
    /// </summary>
    void Enqueue(OutboxRecord record);

    /// <summary>
    /// The relay's batch: unsent records, oldest first, at most
    /// <paramref name="maxCount"/> of them.
    /// </summary>
    /// <remarks>
    /// Bounded because an unbounded read is fine until the day the relay has
    /// been down for an hour, at which point it is a query that loads every
    /// message written in that hour into memory at once.
    /// </remarks>
    Task<IReadOnlyList<OutboxRecord>> ReadUnsentAsync(int maxCount, CancellationToken cancellationToken);

    /// <summary>
    /// Stamps a record as delivered. Idempotent: marking an already-marked or
    /// unknown record is not an error, because a relay that crashed between
    /// delivering and acknowledging will legitimately try again.
    /// </summary>
    Task MarkSentAsync(Guid messageId, CancellationToken cancellationToken);
}

/// <param name="MessageId">
/// The idempotency key, minted at translation time and carried to the
/// subscriber unchanged - the same discipline Day 20's relay established, so
/// that a message published twice is recognised as one message rather than two
/// events.
/// </param>
public sealed record OutboxRecord(
    Guid MessageId,
    string EventType,
    string Payload,
    DateTimeOffset OccurredAt);
