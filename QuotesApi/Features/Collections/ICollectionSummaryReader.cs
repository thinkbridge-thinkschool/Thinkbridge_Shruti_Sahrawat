using MediatR;
using Microsoft.Extensions.Caching.Hybrid;
using QuotesApi.Caching;

namespace QuotesApi.Features.Collections;

/// <summary>
/// The read path the controller calls. Two implementations: one that caches,
/// one that does not.
/// </summary>
/// <remarks>
/// This interface exists so that "with cache" and "without cache" are the same
/// build with a different registration, which is the only way the two halves
/// of Day 21's measurement are comparable. Measuring the uncached number from
/// a previous commit would compare two binaries and call the difference
/// caching.
///
/// Neither implementation contains the query itself. GetCollectionSummariesHandler
/// is still the only place that knows how to read this data, and it was not
/// touched by this exercise - its existing tests still cover it unchanged.
/// </remarks>
public interface ICollectionSummaryReader
{
    Task<IReadOnlyList<CollectionSummary>> GetAsync(
        string? ownerId, int previewSize, CancellationToken ct);
}

/// <summary>
/// Sends the query straight through. The "before" in the before/after.
/// </summary>
/// <remarks>
/// It still records the read, so the two runs produce comparable denominators:
/// with the cache off, reads and factory invocations are equal by definition
/// and the reported hit rate is 0. That is a measured zero rather than an
/// assumed one, which is worth the two lines it costs.
/// </remarks>
public sealed class PassThroughCollectionSummaryReader(IMediator mediator, CacheMetrics metrics)
    : ICollectionSummaryReader
{
    public async Task<IReadOnlyList<CollectionSummary>> GetAsync(
        string? ownerId, int previewSize, CancellationToken ct)
    {
        metrics.RecordRead();
        metrics.RecordFactoryInvocation();
        return await mediator.Send(new GetCollectionSummariesQuery(ownerId, previewSize), ct);
    }
}

/// <summary>
/// The cached read: L1 in-process, L2 Redis when configured, and stampede
/// protection between them.
/// </summary>
/// <remarks>
/// <para><b>What stampede protection actually does here.</b> HybridCache
/// guarantees that concurrent callers asking for the same key run the factory
/// once - the rest await that same in-flight call and are handed its result.
/// So a cold cache hit by 200 simultaneous requests issues the two database
/// queries in GetCollectionSummariesHandler once, not 400 times. That is the
/// difference between a cache miss and a cache miss taking the database down
/// with it, and it is the reason this exercise is not just AddMemoryCache.</para>
///
/// <para><b>Where that guarantee stops.</b> It holds within one HybridCache
/// instance, which means within one process. Two API replicas that both miss
/// at the same moment will each run the factory once - two database hits, not
/// one, and no amount of shared Redis changes that, because the coordination
/// is in-process and the L2 is only storage. For N replicas the worst case is
/// N concurrent factory runs rather than N x concurrency, which is still the
/// difference between a bad second and an outage, but it is not one.</para>
///
/// <para><b>Why the TState overload.</b> The closure-free
/// GetOrCreateAsync&lt;T, TState&gt; is used with a static lambda so the
/// factory delegate is allocated once for the whole application rather than
/// per request. On the hot path this matters precisely because the cache works:
/// once hits dominate, the per-call allocations the cache itself adds are a
/// meaningful share of what is left, and a captured closure would be one of
/// them on every single read including the ones that never run the factory.</para>
/// </remarks>
public sealed class CachedCollectionSummaryReader(
    HybridCache cache,
    IMediator mediator,
    CacheMetrics metrics,
    HybridCacheEntryOptions entryOptions)
    : ICollectionSummaryReader
{
    public async Task<IReadOnlyList<CollectionSummary>> GetAsync(
        string? ownerId, int previewSize, CancellationToken ct)
    {
        metrics.RecordRead();

        return await cache.GetOrCreateAsync(
            CacheKeys.CollectionSummaries(ownerId, previewSize),
            (mediator, metrics, ownerId, previewSize),
            static async (state, token) =>
            {
                // Counted inside the factory rather than around the call:
                // this line runs exactly when the database is about to be
                // asked, so it cannot drift from what actually happened the
                // way a "was it a hit?" check outside the call would.
                state.metrics.RecordFactoryInvocation();

                var summaries = await state.mediator.Send(
                    new GetCollectionSummariesQuery(state.ownerId, state.previewSize), token);

                // Materialised to an array so the cached type is concrete.
                // HybridCache serialises values it cannot prove immutable -
                // including on the L1-only path - and a concrete array is
                // unambiguous to round-trip, where caching the interface would
                // rest on System.Text.Json's support for reconstructing an
                // IReadOnlyList<T> on the way back in. The allocation happens
                // only on a miss, which is the call that just ran two database
                // queries; it is not on the hot path. CollectionSummaryCacheTests
                // exercises this round-trip through a real HybridCache, so a
                // serialisation problem fails a test rather than a request.
                return summaries.ToArray();
            },
            entryOptions,
            CacheKeys.CollectionSummariesTags,
            ct);
    }
}
