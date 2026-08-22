using BenchmarkDotNet.Attributes;
using QuotesApi.Data;
using QuotesApi.Features.Collections;

// Same DbLocator / fresh-context-per-iteration pattern as TrackingBenchmarks,
// applied to the Collections summaries read path: the EF projection handler
// vs the hand-written Dapper SQL handler, against the same e2e-owner data
// used in docs/day12-runtime-evidence.md.
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 15)]
public class CollectionSummaryBenchmarks
{
    private string _dbPath = string.Empty;

    [GlobalSetup]
    public void Setup() => _dbPath = DbLocator.Find();

    [Benchmark(Baseline = true)]
    public async Task<IReadOnlyList<CollectionSummary>> EntityFramework()
    {
        using var ctx = DbLocator.NewContext(_dbPath);
        var handler = new GetCollectionSummariesHandler(ctx);
        return await handler.Handle(
            new GetCollectionSummariesQuery("e2e-owner", 2), CancellationToken.None);
    }

    [Benchmark]
    public async Task<IReadOnlyList<CollectionSummary>> Dapper()
    {
        using var ctx = DbLocator.NewContext(_dbPath);
        var handler = new GetCollectionSummariesDapperHandler(ctx);
        return await handler.Handle(
            new GetCollectionSummariesDapperQuery("e2e-owner", 2), CancellationToken.None);
    }
}
