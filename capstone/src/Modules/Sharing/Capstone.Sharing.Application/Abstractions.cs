namespace Capstone.Sharing.Application;

/// <summary>Who follows whom. Sharing owns this; nothing else does.</summary>
public interface IFollowerDirectory
{
    Task<IReadOnlyList<string>> GetFollowersAsync(string curatorId, CancellationToken cancellationToken);
}

/// <summary>Appends an entry to a follower's feed.</summary>
public interface IFeedWriter
{
    Task AppendAsync(string followerId, FeedEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// Remembers which messages this module has already acted on.
/// </summary>
/// <remarks>
/// Not optional. Delivery is at-least-once, so this handler <i>will</i> be
/// asked to process the same publish twice - a relay retry, a lock renewal
/// that lost a race, a redeploy mid-batch. Without this, the second delivery
/// puts a duplicate card in every follower's feed, which is the kind of bug
/// that looks like a UI glitch and is actually a missing idempotency key.
///
/// Keyed on (MessageId, Consumer) rather than MessageId alone, because two
/// different subscribers must both be allowed to process the same message
/// exactly once each - the same key Day 20's consumer-side dedup used.
/// </remarks>
public interface IProcessedMessageLog
{
    Task<bool> AlreadyHandledAsync(Guid messageId, string consumer, CancellationToken cancellationToken);

    Task MarkHandledAsync(Guid messageId, string consumer, CancellationToken cancellationToken);
}

public sealed record FeedEntry(Guid CollectionId, string CuratorId, string Name, DateTimeOffset PublishedAt);
