using System.Collections.Concurrent;
using Capstone.Sharing.Application;

namespace Capstone.Sharing.Infrastructure;

/// <summary>
/// Scaffold stores. Real ones are a follows table, a feed table partitioned by
/// follower, and the processed-message table Day 20 already has a working
/// shape for.
/// </summary>
public sealed class InMemoryFollowerDirectory : IFollowerDirectory
{
    private readonly ConcurrentDictionary<string, List<string>> _followers = new();

    public void Follow(string curatorId, string followerId)
        => _followers.GetOrAdd(curatorId, _ => []).Add(followerId);

    public Task<IReadOnlyList<string>> GetFollowersAsync(
        string curatorId, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> result = _followers.TryGetValue(curatorId, out var list)
            ? list.ToArray()
            : [];

        return Task.FromResult(result);
    }
}

public sealed class InMemoryFeedWriter : IFeedWriter
{
    private readonly ConcurrentDictionary<string, List<FeedEntry>> _feeds = new();

    public IReadOnlyList<FeedEntry> FeedFor(string followerId)
        => _feeds.TryGetValue(followerId, out var entries) ? entries.ToArray() : [];

    public Task AppendAsync(string followerId, FeedEntry entry, CancellationToken cancellationToken)
    {
        _feeds.GetOrAdd(followerId, _ => []).Add(entry);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryProcessedMessageLog : IProcessedMessageLog
{
    private readonly ConcurrentDictionary<(Guid, string), byte> _handled = new();

    public Task<bool> AlreadyHandledAsync(
        Guid messageId, string consumer, CancellationToken cancellationToken)
        => Task.FromResult(_handled.ContainsKey((messageId, consumer)));

    public Task MarkHandledAsync(Guid messageId, string consumer, CancellationToken cancellationToken)
    {
        _handled[(messageId, consumer)] = 0;
        return Task.CompletedTask;
    }
}
