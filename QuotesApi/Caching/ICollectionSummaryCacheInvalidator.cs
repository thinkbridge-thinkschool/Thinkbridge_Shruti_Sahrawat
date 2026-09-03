using Microsoft.Extensions.Caching.Hybrid;

namespace QuotesApi.Caching;

/// <summary>
/// Drops every cached collection-summary response. Called by the write paths.
/// </summary>
/// <remarks>
/// An interface rather than injecting HybridCache into the controller so that
/// turning the cache off is a registration change and not an "if" statement at
/// every write site - the same reason the read side has a pass-through reader.
/// With the cache disabled the no-op implementation is registered and the
/// write paths keep calling invalidate into nothing, unchanged.
/// </remarks>
public interface ICollectionSummaryCacheInvalidator
{
    Task InvalidateAsync(CancellationToken ct = default);
}

/// <remarks>
/// Tag invalidation in HybridCache is logical, not physical: RemoveByTagAsync
/// records "ignore anything tagged this that was created before now" rather
/// than walking L1 and L2 deleting entries. Stale payloads stay in memory and
/// in Redis until they expire naturally, and are treated as misses when read.
/// That matters in two directions. It is why invalidation is cheap enough to
/// call on every write without thinking about how many keys exist. It is also
/// why the memory is not reclaimed at invalidation time, so an aggressive
/// write rate against a long Expiration leaves more dead payloads in Redis
/// than a naive reading of "remove" would suggest.
/// </remarks>
public sealed class HybridCacheCollectionSummaryInvalidator(HybridCache cache)
    : ICollectionSummaryCacheInvalidator
{
    public async Task InvalidateAsync(CancellationToken ct = default) =>
        await cache.RemoveByTagAsync(CacheKeys.CollectionSummariesTag, ct);
}

public sealed class NoOpCollectionSummaryCacheInvalidator : ICollectionSummaryCacheInvalidator
{
    public Task InvalidateAsync(CancellationToken ct = default) => Task.CompletedTask;
}
