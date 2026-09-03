using Capstone.Curation.Contracts;

namespace Capstone.Sharing.Application;

/// <summary>
/// Fans a published collection out to every follower's feed.
/// </summary>
/// <remarks>
/// The only place Sharing touches Curation, and it does so through
/// <see cref="CollectionPublishedIntegrationEvent"/> - a contract, not a
/// domain type. Sharing cannot reference Curation.Domain even if someone tries;
/// the architecture tests fail the build.
///
/// <b>This is eventually consistent, deliberately.</b> A curator's publish call
/// returns as soon as the collection is saved and the outbox row is written.
/// Feeds catch up afterwards - typically fast, but with no guarantee attached,
/// and if the relay is down they catch up when it returns. The alternative,
/// fanning out inside the publish transaction, buys a stronger promise at the
/// cost of making one curator's publish latency a function of how many
/// followers they have, and of failing the publish outright when the feed store
/// is unavailable. For a feed, that trade is clearly wrong in the direction it
/// is usually made.
///
/// <b>Fan-out on write, and the limit of it.</b> Writing one entry per follower
/// is right while curators have hundreds of followers and wrong once one has
/// millions - the standard answer there is to leave popular curators out of the
/// fan-out and merge their collections in at read time. Not built, but the
/// place it would go is here, and the fact that it is a handler and not a
/// database trigger is what keeps that option open.
/// </remarks>
public sealed class CollectionPublishedHandler(
    IFollowerDirectory followers,
    IFeedWriter feed,
    IProcessedMessageLog processed)
{
    public const string ConsumerName = "sharing.feed-fanout";

    public async Task HandleAsync(
        CollectionPublishedIntegrationEvent message, CancellationToken cancellationToken)
    {
        if (await processed.AlreadyHandledAsync(message.MessageId, ConsumerName, cancellationToken))
        {
            return;
        }

        var recipients = await followers.GetFollowersAsync(message.CuratorId, cancellationToken);

        var entry = new FeedEntry(
            message.CollectionId, message.CuratorId, message.Name, message.PublishedAt);

        foreach (var follower in recipients)
        {
            await feed.AppendAsync(follower, entry, cancellationToken);
        }

        await processed.MarkHandledAsync(message.MessageId, ConsumerName, cancellationToken);
    }
}
