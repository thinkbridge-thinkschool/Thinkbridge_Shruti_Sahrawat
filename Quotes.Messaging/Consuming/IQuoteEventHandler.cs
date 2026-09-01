using Quotes.Messaging.Data;

namespace Quotes.Messaging.Consuming;

/// <summary>
/// What one subscription does with a message.
/// </summary>
public interface IQuoteEventHandler
{
    /// <summary>
    /// The logical consumer name. This is half of the idempotency key, so it
    /// must be stable across restarts and identical across every instance of
    /// the same consumer - it names the <em>role</em>, not the process.
    /// </summary>
    string Consumer { get; }

    /// <summary>
    /// Stages this message's effect on the context.
    /// </summary>
    /// <remarks>
    /// Implementations must NOT call SaveChanges. The dispatcher writes the
    /// handler's changes and the idempotency ledger row in a single
    /// SaveChanges, and that single write is the entire correctness argument -
    /// a handler that saves separately reintroduces the gap the ledger exists
    /// to close.
    /// </remarks>
    /// <exception cref="PoisonMessageException">
    /// The message can never be processed successfully.
    /// </exception>
    Task ApplyAsync(IncomingMessage message, MessagingDbContext db, DateTimeOffset now, CancellationToken cancellationToken);
}
