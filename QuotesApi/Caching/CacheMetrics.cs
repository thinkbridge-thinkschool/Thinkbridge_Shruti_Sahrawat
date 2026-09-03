namespace QuotesApi.Caching;

/// <summary>
/// The counters Day 21's measurement rests on. Process-wide, reset between
/// load-test runs through POST /api/cache/reset.
/// </summary>
/// <remarks>
/// Deliberately counting three separate things rather than one "hit rate",
/// because under a stampede they diverge and the divergence is the entire
/// point:
///
/// <list type="bullet">
/// <item><see cref="RecordRead"/> - one per call into the cached reader. The
/// denominator.</item>
/// <item><see cref="RecordFactoryInvocation"/> - one per time HybridCache
/// actually ran the factory, i.e. per time the database was asked. Under
/// stampede protection 200 concurrent reads produce exactly one of
/// these.</item>
/// <item><see cref="RecordDbCommand"/> - one per command EF actually executed,
/// counted by DbCommandCounterInterceptor. This is the number that cannot be
/// argued with: it is measured at the database boundary, not inferred from
/// what the cache believes about itself.</item>
/// </list>
///
/// A note on the arithmetic, because "hit rate" is easy to overstate here.
/// Reads minus factory invocations is not strictly "reads served from cache" -
/// the waiters that a stampede coalesced onto one in-flight factory call were
/// never in the cache when they arrived. They are reads that did not cause
/// database work, which is the thing worth measuring, so that is what
/// <see cref="CacheMetricsSnapshot.ReadsWithoutDatabaseWork"/> is named.
/// </remarks>
public sealed class CacheMetrics
{
    private long _reads;
    private long _factoryInvocations;
    private long _dbCommands;

    public void RecordRead() => Interlocked.Increment(ref _reads);

    public void RecordFactoryInvocation() => Interlocked.Increment(ref _factoryInvocations);

    public void RecordDbCommand() => Interlocked.Increment(ref _dbCommands);

    public CacheMetricsSnapshot Snapshot()
    {
        // Read each counter once. Reading _reads twice - once for the
        // subtraction and once for the ratio - would let a concurrent request
        // land between them and produce a snapshot whose own numbers disagree.
        var reads = Interlocked.Read(ref _reads);
        var factoryInvocations = Interlocked.Read(ref _factoryInvocations);
        var dbCommands = Interlocked.Read(ref _dbCommands);

        // Clamped because Reset() can land between a read being counted and
        // its factory invocation being counted, zeroing the first while the
        // second is still to come. That makes factoryInvocations briefly
        // exceed reads and the subtraction go negative - a reported hit rate
        // of -100% is a worse answer than a slightly conservative 0%.
        var withoutDatabaseWork = Math.Max(0, reads - factoryInvocations);

        return new CacheMetricsSnapshot(
            Reads: reads,
            FactoryInvocations: factoryInvocations,
            ReadsWithoutDatabaseWork: withoutDatabaseWork,
            HitRate: reads == 0 ? 0d : (double)withoutDatabaseWork / reads,
            DbCommands: dbCommands);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _reads, 0);
        Interlocked.Exchange(ref _factoryInvocations, 0);
        Interlocked.Exchange(ref _dbCommands, 0);
    }
}

public sealed record CacheMetricsSnapshot(
    long Reads,
    long FactoryInvocations,
    long ReadsWithoutDatabaseWork,
    double HitRate,
    long DbCommands);
