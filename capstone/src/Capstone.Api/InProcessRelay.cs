using System.Text.Json;
using Capstone.Curation.Contracts;
using Capstone.Curation.Infrastructure.Outbox;
using Capstone.Sharing.Application;

namespace Capstone.Api;

/// <summary>
/// Moves staged outbox records to their subscribers.
/// </summary>
/// <remarks>
/// In production this is not a class in the API at all: it is a separate
/// process reading the outbox table and publishing to a Service Bus topic,
/// which Days 19 and 20 already built and tested. It lives here, in-process and
/// called explicitly, for exactly one reason - so the scaffold is walkable end
/// to end without standing up a broker, and so the seam it will eventually be
/// replaced at is visible in code rather than described in a document.
///
/// What it deliberately keeps faithful to the real thing: the subscriber
/// receives the serialised integration event and nothing else. It never sees a
/// domain object, never shares memory with the publisher, and is handed a
/// message it must deduplicate itself. Anything that works here works over a
/// broker, because nothing here depends on being in the same process.
/// </remarks>
public sealed class InProcessRelay(
    InMemoryOutboxStore outbox,
    CollectionPublishedHandler sharingHandler)
{
    public async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        var records = outbox.DrainPending();
        var delivered = 0;

        foreach (var record in records)
        {
            if (record.EventType != CollectionPublishedIntegrationEvent.EventType)
            {
                continue;
            }

            var message = JsonSerializer.Deserialize<CollectionPublishedIntegrationEvent>(record.Payload);

            if (message is null)
            {
                continue;
            }

            await sharingHandler.HandleAsync(message, cancellationToken);
            delivered++;
        }

        return delivered;
    }
}
