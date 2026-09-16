namespace Capstone.Curation.Infrastructure.Outbox;

/// <summary>
/// An integration event staged for delivery, as a row.
/// </summary>
/// <remarks>
/// Day 2 of the build plan. The scaffold this replaces was a
/// <c>ConcurrentQueue</c> drained destructively, and that was the flaw the Day
/// 28 design review left open: a record whose handler threw was already gone
/// from the queue, so the retry had nothing to retry. Moving delivery into the
/// background made it worse rather than better, because it removed the 500 the
/// curator used to receive - the only thing that made a lost message
/// noticeable.
///
/// <see cref="SentAt"/> is the whole mechanism. A row is written unsent, the
/// relay delivers it, and only then is it stamped. Nothing deletes anything,
/// so a handler that throws leaves a row that is still unsent, and the next
/// poll finds it again. That is the difference between claiming at-least-once
/// and providing it.
/// </remarks>
public sealed class OutboxMessage
{
    private OutboxMessage()
    {
        // EF Core materialisation only.
    }

    /// <summary>
    /// The idempotency key, and the primary key - they are deliberately the
    /// same value. A separate surrogate id would let the same MessageId be
    /// staged twice without the database objecting, and a subscriber
    /// deduplicating on MessageId would then silently drop the second one as a
    /// redelivery when it was actually a distinct event.
    /// </summary>
    public Guid MessageId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>
    /// Null until a subscriber has accepted it. The relay's only query is
    /// "where this is null, oldest first".
    /// </summary>
    public DateTimeOffset? SentAt { get; private set; }

    public static OutboxMessage Stage(OutboxRecord record) => new()
    {
        MessageId = record.MessageId,
        EventType = record.EventType,
        Payload = record.Payload,
        OccurredAt = record.OccurredAt,
        SentAt = null,
    };

    public void MarkSent(DateTimeOffset sentAt) => SentAt = sentAt;

    public OutboxRecord ToRecord() => new(MessageId, EventType, Payload, OccurredAt);
}
