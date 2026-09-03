namespace QuotesApi.Caching;

/// <summary>
/// How the Day 21 cache is configured. Bound from the "Cache" section.
/// </summary>
/// <remarks>
/// <see cref="Enabled"/> exists so the before/after measurement is a
/// configuration change rather than a code change. The load test in
/// perf/cache-load-test.js is run twice against the same build, once with the
/// cache on and once off, and the only thing that differs between the two runs
/// is this flag - which is the only way the two numbers are comparable at all.
///
/// <see cref="RedisConnectionString"/> is optional on purpose, and that is the
/// same call Day 19 and Day 20 both made about Service Bus: an API that cannot
/// start without a live backing service is one missing resource away from
/// being down. With no connection string configured the cache still runs, L1
/// only, and still gives stampede protection - which is the half of this
/// exercise that does not need Redis at all.
/// </remarks>
public sealed class QuotesCacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>Master switch. False registers the pass-through reader instead.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// StackExchange.Redis connection string for L2. Null or empty means no
    /// L2 is registered and HybridCache runs as an in-process cache.
    /// </summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>Lifetime in L2 (and the overall entry lifetime).</summary>
    public TimeSpan Expiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Lifetime in the in-process L1. Kept shorter than <see cref="Expiration"/>
    /// by default: L1 is per-instance memory, so a short L1 bounds how long two
    /// instances can disagree with each other, while the longer L2 lifetime is
    /// what actually keeps the database idle.
    /// </summary>
    public TimeSpan LocalCacheExpiration { get; set; } = TimeSpan.FromSeconds(30);
}
