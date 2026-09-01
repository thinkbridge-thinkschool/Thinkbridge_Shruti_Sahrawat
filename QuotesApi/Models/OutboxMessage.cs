using System.Text.Json;
using Quotes.Messaging.Contracts;
using Quotes.Messaging.Publishing;

namespace QuotesApi.Models;

/// <summary>
/// One row per domain event that must reach the message broker, written in the
/// same database transaction as the change it describes.
/// </summary>
/// <remarks>
/// This is the write side of the transactional outbox pattern. A database
/// write and a Service Bus publish are two different systems, and nothing can
/// put a network call to a broker inside the same transaction as a SQL insert.
/// What CAN be in that transaction is a row in this table describing the
/// event - an ordinary insert, into the same database, on the same
/// connection as the domain change. So the atomicity question moves from "did
/// the write and the publish both happen", which cannot be answered
/// transactionally, to "did the write and the outbox row both happen", which
/// can, because both are just rows.
///
/// A relay (see Quotes.Outbox) reads unsent rows on its own schedule and
/// publishes them, so the actual network call to Service Bus happens outside
/// any transaction, after the fact, and can be retried freely without putting
/// the domain write at risk. At-least-once delivery from the relay is exactly
/// what Day 19's idempotent consumers already exist to absorb - see
/// <see cref="QuoteEventIds"/> and <c>ProcessedMessage</c>.
///
/// <see cref="MessageId"/> carries the same deterministic id Day 19's
/// publisher already used - derived from the event, not freshly generated per
/// attempt - so a relay that retries after its own crash reproduces the exact
/// id Service Bus and the consumer ledger have already seen, rather than
/// minting a new one that looks like a different event.
/// </remarks>
public class OutboxMessage
{
    public int Id { get; private set; }

    /// <summary>The same deterministic id the relay will publish with.</summary>
    public string MessageId { get; private set; } = string.Empty;

    /// <summary>One of <see cref="QuoteEventTypes"/>.</summary>
    public string EventType { get; private set; } = string.Empty;

    /// <summary>
    /// The event payload, already serialised to JSON text rather than stored
    /// as typed columns. Adding a new event type is then a new
    /// <see cref="QuoteEventTypes"/> constant and a new relay branch, not a
    /// migration on this table.
    /// </summary>
    public string Payload { get; private set; } = string.Empty;

    public DateTime OccurredAt { get; private set; }

    /// <summary>Null until the relay has published this row.</summary>
    public DateTime? SentAt { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private OutboxMessage() { } // Required for EF Core

    /// <param name="messageId">
    /// Must be <see cref="QuoteEventIds.For"/> applied to this same event -
    /// never a freshly generated id - for the same reason the Day 19 publisher
    /// insisted on it: a retry has to produce the same id, not a new one that
    /// looks like a different event to every consumer downstream.
    /// </param>
    public static OutboxMessage For<TPayload>(
        TPayload payload, string eventType, string messageId, DateTimeOffset occurredAt)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            throw new InvalidOperationException("Event type cannot be empty.");

        if (string.IsNullOrWhiteSpace(messageId))
            throw new InvalidOperationException("Message id cannot be empty.");

        return new OutboxMessage
        {
            MessageId = messageId,
            EventType = eventType,
            Payload = JsonSerializer.Serialize(payload, JsonOptions),
            OccurredAt = occurredAt.UtcDateTime,
        };
    }

    /// <summary>
    /// Marks this row published. Safe to call more than once for the same row
    /// - a relay that republishes after its own crash (see
    /// <c>Quotes.Outbox.OutboxRelay</c>) just overwrites the same timestamp
    /// with a later one; nothing downstream reads it as anything but "sent".
    /// </summary>
    public void MarkSent(DateTimeOffset sentAt) => SentAt = sentAt.UtcDateTime;
}
