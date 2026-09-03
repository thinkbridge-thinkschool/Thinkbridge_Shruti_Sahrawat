namespace Capstone.Curation.Infrastructure.Outbox;

/// <summary>
/// Where integration events are staged so they commit with the state change
/// that produced them.
/// </summary>
/// <remarks>
/// A port rather than a direct EF dependency, because the transaction this has
/// to enlist in belongs to whatever persistence Curation ends up using, and
/// that decision is not made yet. The shape is settled though, and it is Day
/// 20's: rows written in the same transaction, a separate relay publishing
/// them afterwards, a unique MessageId so a redelivery deduplicates instead of
/// duplicating.
/// </remarks>
public interface IOutboxStore
{
    void Enqueue(OutboxRecord record);
}

/// <param name="MessageId">
/// The idempotency key, minted here and carried to the subscriber unchanged -
/// the same discipline Day 20's relay established, so that a message published
/// twice is recognised as one message rather than two events.
/// </param>
public sealed record OutboxRecord(
    Guid MessageId,
    string EventType,
    string Payload,
    DateTimeOffset OccurredAt);
