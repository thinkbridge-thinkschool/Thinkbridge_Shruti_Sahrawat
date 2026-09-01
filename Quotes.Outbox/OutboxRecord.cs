namespace Quotes.Outbox;

/// <summary>
/// The relay's own view of one row in QuotesApi's OutboxMessages table.
/// </summary>
/// <remarks>
/// Not a domain entity - a plain persistence record with public setters,
/// because the relay has no business rules about an outbox row beyond "mark
/// it sent". <c>QuotesApi.Models.OutboxMessage</c> is the type that enforces
/// those rules on the write side; this type only has to describe the same
/// five columns so <see cref="OutboxDbContext"/> can read and update them.
/// Two types mapping one physical table is the same shape Day 19 already
/// chose for messaging generally - <c>MessagingDbContext</c> is a separate
/// context from <c>QuotesDbContext</c> because the consumer is a different
/// service with a different lifecycle. Here it is the same service boundary
/// for the same reason, one column narrower: a relay that publishes outbox
/// rows has no legitimate reason to also be able to see a user's password
/// hash, and referencing QuotesApi's whole DbContext would hand it exactly
/// that.
/// </remarks>
public sealed class OutboxRecord
{
    public int Id { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public DateTime? SentAt { get; set; }
}
