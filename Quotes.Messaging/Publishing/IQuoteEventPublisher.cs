namespace Quotes.Messaging.Publishing;

/// <summary>
/// Publishes a domain event to the topic.
/// </summary>
public interface IQuoteEventPublisher
{
    /// <param name="messageId">
    /// The broker-level id used for deduplication. It must be derived from the
    /// event itself, never freshly generated per attempt - see
    /// <see cref="QuoteEventIds"/> for why.
    /// </param>
    Task PublishAsync<T>(T payload, string eventType, string messageId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds the stable message id for an event.
/// </summary>
/// <remarks>
/// This exists to make one rule impossible to get wrong: <b>the message id is a
/// property of the event, not of the send attempt.</b>
///
/// The tempting version is <c>MessageId = Guid.NewGuid()</c>. It looks
/// harmless and it destroys every guarantee downstream. Suppose the publisher
/// sends, the broker accepts, and the acknowledgement is lost to a network
/// blip; the publisher retries. With a fresh guid the broker now holds two
/// messages that are, as far as anything can tell, two different events. The
/// consumer's ledger sees two ids it has never seen, does the work twice, and
/// is entirely correct to do so. No amount of consumer-side care recovers
/// from an id that was already wrong when it left the publisher.
///
/// Deriving the id from the event - type plus entity plus the instant it
/// occurred - means a retry produces byte-identical ids, so the duplicate is
/// recognisable as one.
/// </remarks>
public static class QuoteEventIds
{
    public static string For(string eventType, int quoteId, DateTimeOffset occurredAt)
        => $"{eventType}-{quoteId}-{occurredAt.ToUniversalTime():yyyyMMddTHHmmssfffZ}";
}
