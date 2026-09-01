using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quotes.Messaging.Data;

namespace Quotes.Messaging.Consuming;

public enum DispatchOutcome
{
    /// <summary>First time this consumer saw this message; the work was done.</summary>
    Processed,

    /// <summary>Already handled by this consumer; the work was deliberately not repeated.</summary>
    DuplicateIgnored,
}

/// <summary>
/// Runs one message through one handler, exactly once per consumer.
/// </summary>
/// <remarks>
/// <para><b>Why any of this is needed.</b> Service Bus delivers
/// <em>at least once</em>. Under peek-lock the broker hands a message to a
/// consumer and holds a lock; the message is only removed when the consumer
/// completes it. If the consumer does the work and then dies before completing
/// - or merely takes longer than the lock duration - the lock lapses and the
/// broker redelivers, entirely correctly, because from its side the message was
/// never acknowledged. The handler runs a second time on work that already
/// happened. Exactly-once delivery is not on offer and cannot be configured on;
/// exactly-once <em>effect</em> is something the consumer has to build.</para>
///
/// <para><b>Why the obvious version is wrong.</b> The intuitive fix is
/// check-then-act: ask whether this id has been seen, and if not, do the work
/// and record it. That fails twice over. Two competing consumers can both run
/// the check before either records anything, and both proceed - the window is
/// small, which only means the bug is rare and awful to diagnose. And even with
/// one consumer, a crash between "do the work" and "record it" leaves the work
/// done and unrecorded, so the redelivery does it again. Both holes come from
/// the same cause: the check and the effect are not one operation.</para>
///
/// <para><b>What this does instead.</b> The ledger row and the handler's work
/// are staged on the same context and written by a single SaveChanges, which
/// EF Core executes in one transaction. Either both land or neither does -
/// there is no in-between state to crash into. Uniqueness is enforced by the
/// composite primary key, so the arbitration happens in the database, the one
/// participant that can actually serialise two racing writers. The loser gets
/// a constraint violation, and a constraint violation on the ledger key is not
/// an error - it is the answer.</para>
///
/// <para><b>Where it stops working.</b> The guarantee is exactly as strong as
/// the transaction. A handler that also calls an external API, sends an email
/// or writes to a different database has put an effect outside the transaction,
/// and no ledger can make that atomic - it needs the outbox pattern or a
/// genuinely idempotent downstream. The ledger also grows without bound and
/// needs a retention job; rows older than the longest possible redelivery
/// window are dead weight.</para>
/// </remarks>
public sealed class QuoteEventDispatcher
{
    private readonly MessagingDbContext _db;
    private readonly ILogger<QuoteEventDispatcher> _logger;
    private readonly string _instanceId;
    private readonly TimeProvider _timeProvider;

    public QuoteEventDispatcher(
        MessagingDbContext db,
        ILogger<QuoteEventDispatcher> logger,
        string instanceId,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _logger = logger;
        _instanceId = instanceId;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DispatchOutcome> DispatchAsync(
        IncomingMessage message,
        IQuoteEventHandler handler,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        // Parsing happens before anything is staged, so a malformed body throws
        // PoisonMessageException with nothing half-written behind it.
        await handler.ApplyAsync(message, _db, now, cancellationToken);

        _db.ProcessedMessages.Add(new ProcessedMessage
        {
            MessageId = message.MessageId,
            Consumer = handler.Consumer,
            ProcessedAt = now,
            ProcessedBy = _instanceId,
        });

        try
        {
            // One write. The projection change and the ledger row are in the
            // same transaction by construction.
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A constraint violation here is ambiguous on its face, so confirm
            // it rather than assume. If the ledger row genuinely exists, this
            // was a redelivery and the correct response is to say so and let
            // the caller complete the message. If it does not, something else
            // failed and swallowing it would silently drop real work.
            //
            // Deliberately re-checked against the database rather than matched
            // on a provider-specific error number: SQLite says 19, SQL Server
            // says 2627, and a version bump that changes either turns a
            // correctness guarantee into a silent data-loss bug. Asking the
            // database what is actually there cannot drift.
            if (await AlreadyProcessedAsync(message.MessageId, handler.Consumer, cancellationToken))
            {
                _logger.LogInformation(
                    "Duplicate ignored: {Consumer} had already processed messageId={MessageId} (deliveryCount={DeliveryCount})",
                    handler.Consumer, message.MessageId, message.DeliveryCount);

                return DispatchOutcome.DuplicateIgnored;
            }

            throw;
        }

        _logger.LogInformation(
            "Processed {EventType} messageId={MessageId} by {Consumer} on instance {InstanceId}",
            message.EventType, message.MessageId, handler.Consumer, _instanceId);

        return DispatchOutcome.Processed;
    }

    private Task<bool> AlreadyProcessedAsync(string messageId, string consumer, CancellationToken cancellationToken)
        => _db.ProcessedMessages
              .AsNoTracking()
              .AnyAsync(p => p.MessageId == messageId && p.Consumer == consumer, cancellationToken);
}
