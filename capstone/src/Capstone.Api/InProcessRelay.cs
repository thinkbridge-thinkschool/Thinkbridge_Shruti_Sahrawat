using System.Text.Json;
using Capstone.Curation.Contracts;
using Capstone.Curation.Infrastructure.Outbox;
using Capstone.Sharing.Application;

namespace Capstone.Api;

/// <summary>
/// Reads unsent outbox rows, delivers them to their subscribers, and
/// acknowledges only what was accepted.
/// </summary>
/// <remarks>
/// In production this is not a class in the API at all: it is a separate
/// process reading the outbox table and publishing to a Service Bus topic,
/// which Days 19 and 20 already built and tested. It lives here, in-process,
/// for exactly one reason - so the scaffold is walkable end to end without
/// standing up a broker, and so the seam it will eventually be replaced at is
/// visible in code rather than described in a document. Day 3 of the build
/// plan makes that swap, and by design the only thing that changes is where
/// this reads from and where it publishes to.
///
/// What it deliberately keeps faithful to the real thing: the subscriber
/// receives the serialised integration event and nothing else. It never sees a
/// domain object, never shares memory with the publisher, and is handed a
/// message it must deduplicate itself. Anything that works here works over a
/// broker, because nothing here depends on being in the same process.
///
/// <b>What changed on day 2.</b> The drain is no longer destructive. It used
/// to dequeue from an in-memory queue, which meant a handler that threw took
/// the record with it and the retry had nothing left to retry - the flaw the
/// Day 28 design review left open, and the reason moving delivery into the
/// background had made things worse rather than better. Now a row is read
/// while unsent, delivered, and stamped afterwards. A throw anywhere in
/// between leaves the row exactly as it was, and the next poll picks it up.
/// </remarks>
public sealed class InProcessRelay(
    IOutboxStore outbox,
    CollectionPublishedHandler sharingHandler)
{
    /// <summary>
    /// How many rows one poll will attempt.
    /// </summary>
    /// <remarks>
    /// Bounded for the reason every batch is bounded: the interesting case is
    /// not the steady state, it is the first poll after the relay has been
    /// down. Unbounded, that poll reads the entire backlog into memory and
    /// then holds every delivery behind one transaction-sized blast radius.
    /// </remarks>
    public const int BatchSize = 20;

    public async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        var records = await outbox.ReadUnsentAsync(BatchSize, cancellationToken);
        var delivered = 0;

        foreach (var record in records)
        {
            // A row this relay cannot interpret. In practice that means a
            // deploy skew: a writer staging an event type this reader does not
            // know yet. Acknowledged rather than left unsent, because
            // ReadUnsentAsync is ordered oldest-first and an un-deliverable row
            // at the head of the queue would block every message behind it
            // forever. Dropping it is the wrong answer too - the right one is
            // the dead-letter queue Day 19 already built, which is where this
            // goes once the relay reads a broker instead of a table.
            if (record.EventType != CollectionPublishedIntegrationEvent.EventType)
            {
                await outbox.MarkSentAsync(record.MessageId, cancellationToken);
                continue;
            }

            var message = JsonSerializer.Deserialize<CollectionPublishedIntegrationEvent>(record.Payload);

            if (message is null)
            {
                await outbox.MarkSentAsync(record.MessageId, cancellationToken);
                continue;
            }

            // Order matters and is the whole design. Deliver first, then
            // acknowledge. Reversed, a subscriber failure would leave a row
            // marked sent that nobody ever received, which is at-most-once
            // wearing an outbox as a disguise.
            await sharingHandler.HandleAsync(message, cancellationToken);

            await outbox.MarkSentAsync(record.MessageId, cancellationToken);

            delivered++;
        }

        return delivered;
    }
}
