using QuotesApi.Caching;

namespace QuotesApi.Extensions;

/// <summary>
/// The two endpoints perf/cache-load-test.js drives the measurement through.
/// </summary>
/// <remarks>
/// Anonymous and unauthenticated, which is a deliberate limitation rather than
/// an oversight: these exist to be scraped by a load-test script, and putting
/// a token in front of them would mean the script's setup and teardown carry
/// credentials for no benefit. They expose counters and nothing else - no
/// query results, no user data, no configuration secrets - but the reset
/// endpoint is still a write anybody can call, so this is a development
/// affordance and would need gating behind an environment check or an admin
/// policy before it went anywhere real. Left ungated here so the exercise is
/// reproducible with one command.
/// </remarks>
public static class CacheDiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapCacheDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cache/stats", (CacheMetrics metrics, QuotesCacheOptions options) =>
        {
            var snapshot = metrics.Snapshot();

            return Results.Ok(new
            {
                cacheEnabled = options.Enabled,
                l2 = string.IsNullOrWhiteSpace(options.RedisConnectionString) ? "none" : "redis",
                expiration = options.Expiration.ToString(),
                localCacheExpiration = options.LocalCacheExpiration.ToString(),
                reads = snapshot.Reads,
                factoryInvocations = snapshot.FactoryInvocations,
                readsWithoutDatabaseWork = snapshot.ReadsWithoutDatabaseWork,
                hitRate = Math.Round(snapshot.HitRate, 4),
                dbCommands = snapshot.DbCommands,
            });
        })
        .WithName("GetCacheStats")
        .AllowAnonymous();

        app.MapPost("/api/cache/reset", (CacheMetrics metrics) =>
        {
            // Counters only. The cache itself is deliberately not cleared here:
            // a load test that reset the counters and the cache together could
            // not tell "the cache was warm and served everything" apart from
            // "the cache was empty and one factory call served everything",
            // and those are different results. Emptying the cache is what
            // restarting the API, or waiting out the expiry, is for.
            metrics.Reset();
            return Results.NoContent();
        })
        .WithName("ResetCacheStats")
        .AllowAnonymous();

        return app;
    }
}
