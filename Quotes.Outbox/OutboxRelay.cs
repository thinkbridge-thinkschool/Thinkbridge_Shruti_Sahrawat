using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quotes.Messaging.Contracts;
using Quotes.Messaging.Publishing;

namespace Quotes.Outbox;

public enum OutboxRelayOutcome
{
    /// <summary>Reached the broker and the row is now marked sent.</summary>
    Published,

    /// <summary>
    /// The publish attempt threw. The row is left unsent on purpose - see the
    /// remarks on <see cref="OutboxRelay"/> for why that is the safe choice
    /// rather than a bug to fix.
    /// </summary>
    Failed,
}

public sealed record OutboxRelayResult(int OutboxId, string MessageId, string EventType, OutboxRelayOutcome Outcome, Exception? Error = null);

/// <summary>
/// Reads unsent outbox rows and publishes them. This is the half of Day 20
/// that turns "a row exists describing an event" into "the event actually
/// reached Service Bus".
/// </summary>
/// <remarks>
/// <para><b>The crash this exists to survive.</b> A row can be published and
/// then the process can die before the SaveChanges that marks it sent
/// commits - between the network call returning and the disk write landing
/// there is no atomic way to make both happen or neither. So on the next
/// poll that row is still unsent and gets republished. That is not a
/// bug in this relay; it is what "at-least-once" means, and it is exactly the
/// case Day 19 built <c>QuoteEventDispatcher</c> and the
/// <c>(MessageId, Consumer)</c> ledger to absorb - a consumer that sees the
/// same <see cref="OutboxRecord.MessageId"/> twice does the work once. The
/// relay is allowed to be careless about exactly-once because the consumer
/// was already built not to need it.</para>
///
/// <para><b>Why this cannot lose a message instead.</b> A row only exists
/// here once <c>QuoteRepository</c> has committed it in the same transaction
/// as the domain change (see QuotesApi/Repositories/QuoteRepository.cs), so
/// by the time this relay can see a row at all, the event it describes has
/// already durably happened. From there the row's only two states are
/// "unsent" and "sent", and every attempt in between either flips it to sent
/// or leaves it exactly as it was for the next poll to try again. There is no
/// state a row can end up in where the event happened, the relay gave up, and
/// nothing will ever retry it - see <c>OutboxRelayTests</c> for the test that
/// forces a mid-publish failure and confirms the row survives unsent.</para>
///
/// <para><b>Where it stops working.</b> Two relay instances running at once
/// would both pick up the same unsent row and both publish it - safe for the
/// same idempotency reason above, but wasted work this single-instance design
/// does not need to pay for. Claiming a row before publishing it (an
/// UPDATE ... WHERE SentAt IS NULL affecting exactly one writer, the same
/// shape as Day 19's ledger insert) is the fix, and is out of scope for what
/// this exercise asked for.</para>
/// </remarks>
public sealed class OutboxRelay
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly OutboxDbContext _db;
    private readonly IQuoteEventPublisher _publisher;
    private readonly ILogger<OutboxRelay> _logger;
    private readonly TimeProvider _timeProvider;

    public OutboxRelay(
        OutboxDbContext db,
        IQuoteEventPublisher publisher,
        ILogger<OutboxRelay> logger,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _publisher = publisher;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Publishes up to <paramref name="batchSize"/> unsent rows, oldest first.
    /// Each row's SaveChanges is meant to succeed or fail on its own, so a
    /// failure partway through the batch leaves every row before it marked
    /// sent and every row from it onward untouched for the next poll - never
    /// a mix of "half written". That only holds because a failed row's entry
    /// is explicitly reset to Unchanged in the catch below: all rows share
    /// one <see cref="OutboxDbContext"/> and its change tracker, so without
    /// that reset a row whose own SaveChanges just threw would stay Modified
    /// and ride along on - or drag down - the next row's successful save.
    /// </summary>
    public async Task<IReadOnlyList<OutboxRelayResult>> RelayBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        var pending = await _db.Outbox
            .Where(o => o.SentAt == null)
            .OrderBy(o => o.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var results = new List<OutboxRelayResult>(pending.Count);

        foreach (var row in pending)
        {
            try
            {
                await PublishOneAsync(row, cancellationToken);

                row.SentAt = _timeProvider.GetUtcNow().UtcDateTime;
                await _db.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Relayed {EventType} messageId={MessageId} (outbox id {OutboxId})",
                    row.EventType, row.MessageId, row.Id);
                results.Add(new OutboxRelayResult(row.Id, row.MessageId, row.EventType, OutboxRelayOutcome.Published));
            }
            catch (Exception ex)
            {
                // If PublishOneAsync itself threw, row.SentAt was never touched and
                // the entity is already Unchanged - this is a no-op. But if the
                // publish succeeded and it was this row's own SaveChangesAsync that
                // threw, row.SentAt is still set in memory and the entity is still
                // tracked as Modified. Left alone, that dirty entity rides along on
                // the next row's successful SaveChangesAsync in this same
                // context - silently marking a row reported here as Failed as
                // "sent" in the database, or worse, dragging a healthy row's save
                // down with it if row's own data is what made the save fail.
                // Reverting the in-memory value and detaching the entity is what
                // keeps this row's fate solely in this catch block's hands.
                row.SentAt = null;
                _db.Entry(row).State = EntityState.Unchanged;

                _logger.LogError(
                    ex,
                    "Publish failed for outbox id {OutboxId} messageId={MessageId}; leaving unsent for retry",
                    row.Id, row.MessageId);
                results.Add(new OutboxRelayResult(row.Id, row.MessageId, row.EventType, OutboxRelayOutcome.Failed, ex));
            }
        }

        return results;
    }

    /// <summary>
    /// Maps an outbox row back to the typed record <see cref="IQuoteEventPublisher"/>
    /// expects, by <see cref="OutboxRecord.EventType"/>.
    /// </summary>
    /// <remarks>
    /// Deserialising the stored JSON and handing the publisher a typed record
    /// - rather than the raw bytes - keeps <c>ServiceBusQuoteEventPublisher</c>
    /// as the one place that shapes an actual ServiceBusMessage, unchanged
    /// from Day 19. The cost is a round trip through JSON the relay did not
    /// need to make; the benefit is that this relay adds no new publisher
    /// contract for Day 19's existing one to drift against.
    /// </remarks>
    private async Task PublishOneAsync(OutboxRecord row, CancellationToken cancellationToken)
    {
        switch (row.EventType)
        {
            case QuoteEventTypes.QuoteCreated:
                var created = JsonSerializer.Deserialize<QuoteCreated>(row.Payload, JsonOptions)
                              ?? throw new InvalidOperationException($"Outbox row {row.Id} payload deserialised to null.");
                await _publisher.PublishAsync(created, QuoteEventTypes.QuoteCreated, row.MessageId, cancellationToken);
                break;

            case QuoteEventTypes.QuoteDeleted:
                var deleted = JsonSerializer.Deserialize<QuoteDeleted>(row.Payload, JsonOptions)
                              ?? throw new InvalidOperationException($"Outbox row {row.Id} payload deserialised to null.");
                await _publisher.PublishAsync(deleted, QuoteEventTypes.QuoteDeleted, row.MessageId, cancellationToken);
                break;

            default:
                // Unrecognised rather than silently skipped: a relay that
                // quietly ignores a row it does not understand is a message
                // that looks lost to everyone downstream, with nothing in any
                // log to say why. Left unsent, same as any other failure, so
                // it stays visible to whatever polls "how many unsent rows are
                // there" rather than disappearing.
                throw new InvalidOperationException(
                    $"Outbox row {row.Id} has unrecognised EventType '{row.EventType}'.");
        }
    }
}
