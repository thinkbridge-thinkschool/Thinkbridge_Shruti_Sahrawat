using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using QuotesApi.Caching;
using QuotesApi.Features.Collections;

namespace Quotes.Tests.Unit;

/// <summary>
/// Day 21: does the cache actually collapse a stampede, and does a write
/// actually invalidate it.
/// </summary>
/// <remarks>
/// Against a real <see cref="HybridCache"/> resolved from the real DI
/// extension, not a substitute for it. A mocked cache would be asserting that
/// this test's own mock deduplicates concurrent callers, which proves nothing
/// about the library that has to do it in production - and stampede protection
/// is precisely the behaviour being bought here, so faking it would fake the
/// whole exercise. Same reasoning as Day 20 using a real SQLite file rather
/// than EF's InMemory provider: the guarantee under test lives in the real
/// component, so the real component is in the test.
///
/// MediatR is substituted, because it is not what is under test - it stands in
/// for "the database answered", and only the number of times it is asked
/// matters.
/// </remarks>
public sealed class CollectionSummaryCacheTests : IDisposable
{
    // Each BuildCache call stands up a real container; HybridCache's backing
    // MemoryCache owns timers, so the providers are kept and disposed rather
    // than left to the GC for the length of the run.
    private readonly List<ServiceProvider> _providers = [];

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
    }

    private static readonly DateTime AddedAt = new(2026, 3, 14, 9, 30, 0, DateTimeKind.Utc);

    private static IReadOnlyList<CollectionSummary> SampleSummaries(int itemCount = 2) =>
    [
        new CollectionSummary(
            1, "Computing Pioneers", "owner-1", itemCount, AddedAt,
            [
                new CollectionPreviewItem(
                    10, "Ada Lovelace", "The Analytical Engine weaves algebraic patterns.", AddedAt),
            ]),
    ];

    /// <summary>
    /// Counts how many times the query ran, and can hold every caller inside
    /// the factory at once so a stampede is provably concurrent rather than
    /// accidentally sequential.
    /// </summary>
    private sealed class QueryGate
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Completes as soon as the first caller is inside the factory.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<CollectionSummary> Result { get; set; } = SampleSummaries();

        /// <summary>Lets the factory return. Call before the read to run ungated.</summary>
        public void Release() => _release.TrySetResult();

        public async Task<IReadOnlyList<CollectionSummary>> InvokeAsync()
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            await _release.Task;
            return Result;
        }
    }

    private static (IMediator Mediator, QueryGate Gate) GatedMediator()
    {
        var gate = new QueryGate();
        var mediator = Substitute.For<IMediator>();

        mediator
            .Send(Arg.Any<GetCollectionSummariesQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.InvokeAsync());

        return (mediator, gate);
    }

    private (HybridCache Cache, CacheMetrics Metrics, HybridCacheEntryOptions Options) BuildCache()
    {
        var services = new ServiceCollection();

        // AddLogging because HybridCache's implementation takes an ILogger.
        // AddHybridCache brings its own IMemoryCache and options, but a bare
        // ServiceCollection has no logging in it and resolving HybridCache
        // would fail on that rather than on anything this test cares about.
        services.AddLogging();
        services.AddHybridCache();

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        var entryOptions = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(5),
            LocalCacheExpiration = TimeSpan.FromMinutes(5),
        };

        return (provider.GetRequiredService<HybridCache>(), new CacheMetrics(), entryOptions);
    }

    /// <summary>
    /// The exercise's central claim: a cache miss hit by N concurrent readers
    /// must produce one database fetch, not N.
    /// </summary>
    /// <remarks>
    /// The gate is what makes this a stampede rather than a sequence. Without
    /// it the first caller would finish and populate the cache before the
    /// others were scheduled, and the test would pass against a warm cache
    /// while proving nothing about concurrency. Here all 50 callers are
    /// launched, the test waits until one is provably inside the factory, and
    /// only then is the factory allowed to return - so every caller was in
    /// flight against an empty cache at the same moment.
    /// </remarks>
    [Fact]
    public async Task ConcurrentReadsOnAColdCache_RunTheQueryOnce()
    {
        const int concurrentReaders = 50;

        var (cache, metrics, options) = BuildCache();
        var (mediator, gate) = GatedMediator();
        var reader = new CachedCollectionSummaryReader(cache, mediator, metrics, options);

        // Every reader signals immediately before it calls in, so the test can
        // wait for all 50 to have arrived rather than releasing on the first
        // one. Releasing on the first would leave a window where a straggler
        // had not reached GetOrCreateAsync yet, found the entry already
        // published, and triggered a second factory call - the test would then
        // fail for a scheduling reason rather than a real regression.
        using var allArrived = new CountdownEvent(concurrentReaders);

        var readers = Enumerable
            .Range(0, concurrentReaders)
            .Select(_ => Task.Run(() =>
            {
                allArrived.Signal();
                return reader.GetAsync(ownerId: null, previewSize: 3, CancellationToken.None);
            }))
            .ToArray();

        allArrived.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("all readers should have started");

        // One of them is provably inside the factory, and the factory cannot
        // return until released - so every reader that has arrived is either
        // in the factory or waiting on it.
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // A short settle so the readers that signalled last are inside
        // GetOrCreateAsync, not merely past the signal. The factory is still
        // held open throughout, so this cannot let anyone through early.
        await Task.Delay(250);

        gate.Release();

        var results = await Task.WhenAll(readers);

        gate.Calls.Should().Be(
            1, "stampede protection must collapse concurrent misses for one key into a single factory call");

        var snapshot = metrics.Snapshot();
        snapshot.Reads.Should().Be(concurrentReaders);
        snapshot.FactoryInvocations.Should().Be(1);
        snapshot.ReadsWithoutDatabaseWork.Should().Be(concurrentReaders - 1);

        results.Should().HaveCount(concurrentReaders);
        results.Should().OnlyContain(r => r.Count == 1);
    }

    /// <summary>
    /// The round-trip the array-vs-interface decision in
    /// CachedCollectionSummaryReader rests on.
    /// </summary>
    /// <remarks>
    /// HybridCache serialises values it cannot prove immutable, so a second
    /// read comes back through the serialiser rather than being handed the
    /// same object. That makes this a real test of whether CollectionSummary -
    /// a record with a nested IReadOnlyList of another record - survives being
    /// written and read back. If it does not, this fails here rather than as a
    /// 500 on a cache hit in production.
    /// </remarks>
    [Fact]
    public async Task ASecondRead_ComesBackFromTheCacheIntact()
    {
        var (cache, metrics, options) = BuildCache();
        var (mediator, gate) = GatedMediator();
        gate.Release();

        var reader = new CachedCollectionSummaryReader(cache, mediator, metrics, options);

        var first = await reader.GetAsync(null, 3, CancellationToken.None);
        var second = await reader.GetAsync(null, 3, CancellationToken.None);

        gate.Calls.Should().Be(1, "the second read must be served by the cache");

        second.Should().BeEquivalentTo(first);

        var only = second.Single();
        only.Name.Should().Be("Computing Pioneers");
        only.ItemCount.Should().Be(2);
        only.Preview.Should().ContainSingle()
            .Which.Author.Should().Be("Ada Lovelace");
    }

    /// <summary>
    /// Different preview sizes are different questions and must not share a
    /// cached answer.
    /// </summary>
    [Fact]
    public async Task ADifferentPreviewSize_IsADifferentCacheEntry()
    {
        var (cache, metrics, options) = BuildCache();
        var (mediator, gate) = GatedMediator();
        gate.Release();

        var reader = new CachedCollectionSummaryReader(cache, mediator, metrics, options);

        await reader.GetAsync(null, previewSize: 3, CancellationToken.None);
        await reader.GetAsync(null, previewSize: 1, CancellationToken.None);

        gate.Calls.Should().Be(2, "previewSize changes the answer, so it has to be part of the key");
    }

    /// <summary>
    /// A write has to make the next read go back to the database.
    /// </summary>
    /// <remarks>
    /// This is the half that stops the cache from being a correctness bug.
    /// Invalidation goes through the same tag the reader writes with - the
    /// reason both live in CacheKeys rather than being spelled out at each
    /// site.
    /// </remarks>
    [Fact]
    public async Task AfterInvalidation_TheNextReadGoesBackToTheDatabase()
    {
        var (cache, metrics, options) = BuildCache();
        var (mediator, gate) = GatedMediator();
        gate.Release();

        var reader = new CachedCollectionSummaryReader(cache, mediator, metrics, options);
        var invalidator = new HybridCacheCollectionSummaryInvalidator(cache);

        await reader.GetAsync(null, 3, CancellationToken.None);
        await reader.GetAsync(null, 3, CancellationToken.None);
        gate.Calls.Should().Be(1);

        // The write side: something changed, so every cached summary is stale.
        await invalidator.InvalidateAsync(CancellationToken.None);
        gate.Result = SampleSummaries(itemCount: 3);

        var afterWrite = await reader.GetAsync(null, 3, CancellationToken.None);

        gate.Calls.Should().Be(2, "invalidation must force the next read back to the source");
        afterWrite.Single().ItemCount.Should().Be(3, "the read after a write must see the write");
    }

    /// <summary>
    /// With the cache off, every read is a database read - the "before" side
    /// of the measurement, asserted rather than assumed.
    /// </summary>
    [Fact]
    public async Task WithTheCacheDisabled_EveryReadHitsTheDatabase()
    {
        var metrics = new CacheMetrics();
        var (mediator, gate) = GatedMediator();
        gate.Release();

        var reader = new PassThroughCollectionSummaryReader(mediator, metrics);

        await reader.GetAsync(null, 3, CancellationToken.None);
        await reader.GetAsync(null, 3, CancellationToken.None);
        await reader.GetAsync(null, 3, CancellationToken.None);

        gate.Calls.Should().Be(3);

        var snapshot = metrics.Snapshot();
        snapshot.Reads.Should().Be(3);
        snapshot.FactoryInvocations.Should().Be(3);
        snapshot.HitRate.Should().Be(0d);
    }
}
