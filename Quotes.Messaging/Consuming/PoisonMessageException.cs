namespace Quotes.Messaging.Consuming;

/// <summary>
/// Thrown when a message can never succeed, no matter how many times it is
/// redelivered - a malformed body, a missing required field, a schema version
/// nothing here understands.
/// </summary>
/// <remarks>
/// This type exists to separate the two kinds of failure a handler can hit,
/// because they deserve opposite responses.
///
/// A <em>transient</em> failure - the database is briefly unreachable, a
/// downstream call times out - should be retried, because the next attempt
/// might well succeed. Abandoning the message puts it back for redelivery, and
/// if it keeps failing the broker eventually dead-letters it on delivery count.
///
/// A <em>deterministic</em> failure has no next attempt worth making. The body
/// is not going to become valid JSON on the fourth try. Feeding it through the
/// retry machinery wastes the lock duration times the delivery count, keeps a
/// consumer busy on work that cannot complete, and buries the real diagnosis
/// under identical repeated errors. Recognising it and dead-lettering
/// immediately - with a reason attached - turns a slow silent failure into an
/// item on a queue somebody can go and look at.
/// </remarks>
public sealed class PoisonMessageException : Exception
{
    public PoisonMessageException(string reason, string description)
        : base($"{reason}: {description}")
    {
        Reason = reason;
        Description = description;
    }

    /// <summary>Short code, surfaced as the message's DeadLetterReason.</summary>
    public string Reason { get; }

    /// <summary>Detail, surfaced as the message's DeadLetterErrorDescription.</summary>
    public string Description { get; }
}
