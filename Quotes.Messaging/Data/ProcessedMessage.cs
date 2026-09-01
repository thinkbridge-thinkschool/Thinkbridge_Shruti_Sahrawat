namespace Quotes.Messaging.Data;

/// <summary>
/// One row per (message, consumer) pair that has been successfully handled.
/// This is the idempotency ledger.
/// </summary>
/// <remarks>
/// The primary key is deliberately <em>composite</em>: <see cref="MessageId"/>
/// alone is not enough.
///
/// A topic fans one published message out to every subscription, so the same
/// MessageId legitimately arrives at <c>search-indexer</c> and at
/// <c>audit-log</c>, and both are supposed to do their own work with it. Key
/// the ledger on MessageId alone and whichever consumer happens to run second
/// sees a row already there, concludes "duplicate", and silently skips work it
/// was always meant to do. The bug is invisible: no error, no retry, no
/// dead-letter - one subscription just quietly stops having any effect.
///
/// Keying on (MessageId, Consumer) says the real rule: each consumer processes
/// each message at most once, independently of what any other consumer did.
/// </remarks>
public sealed class ProcessedMessage
{
    /// <summary>The broker's message id, as stamped by the publisher.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>Which logical consumer processed it, e.g. "search-indexer".</summary>
    public string Consumer { get; set; } = string.Empty;

    public DateTimeOffset ProcessedAt { get; set; }

    /// <summary>
    /// Which worker instance won the race. Purely diagnostic - it is what makes
    /// competing consumers visible in the data rather than only in logs.
    /// </summary>
    public string ProcessedBy { get; set; } = string.Empty;
}
