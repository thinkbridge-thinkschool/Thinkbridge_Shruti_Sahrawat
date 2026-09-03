using System.Text.Json;
using Capstone.Curation.Contracts;
using Capstone.Curation.Domain.Events;
using Capstone.SharedKernel;

namespace Capstone.Curation.Infrastructure.Outbox;

/// <summary>
/// Turns Curation's internal facts into the contract other modules subscribe to.
/// </summary>
/// <remarks>
/// This class is the module boundary, expressed as code. It is the only place
/// that knows both the domain event and the integration event, and that is why
/// the aggregate can be refactored freely without breaking a subscriber: the
/// blast radius of a domain change stops here, at a compile error in one file,
/// instead of reaching a running consumer at three in the morning.
///
/// Unknown domain events are dropped rather than thrown on. Not every internal
/// fact is something the outside world has any business hearing about, and
/// requiring a translation for each one would push Curation towards publishing
/// its whole model by default.
/// </remarks>
public static class DomainEventTranslator
{
    public static OutboxRecord? ToOutboxRecord(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case CollectionPublished published:
            {
                var messageId = Guid.CreateVersion7();

                var payload = new CollectionPublishedIntegrationEvent(
                    MessageId: messageId,
                    CollectionId: published.CollectionId.Value,
                    CuratorId: published.CuratorId.Value,
                    Name: published.Name,
                    QuoteIds: published.QuoteIds.Select(id => id.Value).ToArray(),
                    PublishedAt: published.OccurredAt);

                return new OutboxRecord(
                    messageId,
                    CollectionPublishedIntegrationEvent.EventType,
                    JsonSerializer.Serialize(payload),
                    published.OccurredAt);
            }

            default:
                return null;
        }
    }
}
