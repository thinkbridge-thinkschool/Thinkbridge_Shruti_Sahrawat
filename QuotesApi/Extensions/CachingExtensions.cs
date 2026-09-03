using Microsoft.Extensions.Caching.Hybrid;
// Explicit, though the Web SDK's implicit usings already include it:
// AddHybridCache and AddStackExchangeRedisCache are declared in this
// namespace but ship in the two Day 21 packages' own assemblies, so stating
// it here makes the dependency visible at the top of the file rather than
// leaving a reader to infer it from a build error.
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Caching;
using QuotesApi.Features.Collections;

namespace QuotesApi.Extensions;

public static class CachingExtensions
{
    /// <summary>
    /// Day 21: HybridCache in front of the collection-summaries read.
    /// </summary>
    /// <remarks>
    /// Three things are decided here, and each is a configuration value rather
    /// than a code path so that the load test can change its mind between runs
    /// without a rebuild:
    ///
    /// <list type="number">
    /// <item><b>Whether there is an L2 at all.</b> A Redis connection string
    /// registers StackExchangeRedisCache, which HybridCache picks up as its
    /// secondary tier automatically - it uses whatever IDistributedCache is in
    /// the container. No connection string means no L2 registration and
    /// HybridCache runs in-process only, still with stampede protection. The
    /// API starts either way, which is the same refusal to hard-depend on a
    /// live backing service that kept Day 19's publisher out of this host.</item>
    ///
    /// <item><b>Whether the read is cached.</b> Cache:Enabled picks the
    /// implementation of ICollectionSummaryReader. Off registers the
    /// pass-through and the no-op invalidator, so the write paths keep calling
    /// invalidate and nothing happens.</item>
    ///
    /// <item><b>How long entries live</b>, separately for L1 and L2 - see
    /// QuotesCacheOptions.</item>
    /// </list>
    ///
    /// AddHybridCache is called unconditionally, even when the cache is
    /// disabled. Registering a service nobody resolves costs nothing, and the
    /// alternative - a conditional registration - would mean the disabled
    /// configuration exercises a different container shape than the enabled
    /// one, so a DI mistake would only show up in whichever mode was not run
    /// last.
    /// </remarks>
    public static IServiceCollection AddQuotesCaching(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = new QuotesCacheOptions();
        configuration.GetSection(QuotesCacheOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        // Process-wide counters, and the EF interceptor that feeds the one
        // that is measured at the database rather than reported by the cache.
        services.AddSingleton<CacheMetrics>();
        services.AddSingleton<DbCommandCounterInterceptor>();

        if (!string.IsNullOrWhiteSpace(options.RedisConnectionString))
        {
            services.AddStackExchangeRedisCache(redis =>
            {
                redis.Configuration = options.RedisConnectionString;
                redis.InstanceName = "quotes:";
            });
        }

        var entryOptions = new HybridCacheEntryOptions
        {
            Expiration = options.Expiration,
            LocalCacheExpiration = options.LocalCacheExpiration,
        };

        services.AddSingleton(entryOptions);

        services.AddHybridCache(hybrid =>
        {
            hybrid.DefaultEntryOptions = entryOptions;
        });

        if (options.Enabled)
        {
            services.AddScoped<ICollectionSummaryReader, CachedCollectionSummaryReader>();
            services.AddScoped<ICollectionSummaryCacheInvalidator, HybridCacheCollectionSummaryInvalidator>();
        }
        else
        {
            services.AddScoped<ICollectionSummaryReader, PassThroughCollectionSummaryReader>();
            services.AddScoped<ICollectionSummaryCacheInvalidator, NoOpCollectionSummaryCacheInvalidator>();
        }

        return services;
    }
}
