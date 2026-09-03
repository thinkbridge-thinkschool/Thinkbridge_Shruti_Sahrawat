namespace Capstone.Curation.Contracts;

/// <summary>
/// Curation announcing, to anyone who subscribes, that a collection went live.
/// </summary>
/// <remarks>
/// A separate type from the <c>CollectionPublished</c> domain event on purpose,
/// even though the two currently carry nearly the same fields, and the
/// duplication is the point. The domain event is Curation's internal fact and
/// changes whenever Curation's model changes; this is a wire contract other
/// modules have taken a dependency on, and it may only change in ways
/// subscribers can absorb. Publishing the domain event directly would weld
/// those two rates of change together, and the first refactor of the aggregate
/// would silently become a breaking change for every subscriber.
///
/// The ids are primitives here rather than the domain's typed ids for the same
/// reason: a subscriber should not need to reference Curation.Domain to
/// deserialise a message, and if it did, the boundary would be a fiction.
///
/// <see cref="MessageId"/> is the deduplication key. Delivery is at-least-once
/// - Day 19 and Day 20 established that and built the consumer-side dedup to
/// match - so every subscriber is expected to be idempotent on this value.
/// </remarks>
public sealed record CollectionPublishedIntegrationEvent(
    Guid MessageId,
    Guid CollectionId,
    string CuratorId,
    string Name,
    IReadOnlyList<int> QuoteIds,
    DateTimeOffset PublishedAt)
{
    public const string EventType = "curation.collection.published.v1";
}
