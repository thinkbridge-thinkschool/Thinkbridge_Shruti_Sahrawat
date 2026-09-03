using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly.CircuitBreaker;
using QuotesApi.Resilience;

namespace QuotesApi.Extensions;

public static class ResilienceExtensions
{
    /// <summary>
    /// Day 22: the outbound dependency, behind a Polly pipeline.
    /// </summary>
    /// <remarks>
    /// Three of the registrations here are singletons constructed eagerly and
    /// registered as instances rather than by type, which is the same shape
    /// AddQuotesCaching uses and for the same reason: the pipeline is built
    /// during registration, before any service provider exists, so the objects
    /// its callbacks close over have to be in hand at that moment.
    ///
    /// The CircuitBreakerStateProvider is the interesting one. Polly writes the
    /// current circuit state into it, and registering the same instance in DI
    /// is what lets GET /api/resilience/stats report Closed, Open or HalfOpen
    /// as a fact read from the breaker rather than as a state this code tried
    /// to mirror alongside it. A second copy of that state would be wrong
    /// eventually, and wrong in a way nobody would notice until the demo.
    ///
    /// HttpClient.Timeout is disabled deliberately. Its default 100 seconds is
    /// a competing deadline that Polly knows nothing about, and two timeouts
    /// that disagree mean the effective budget is whichever fires first - not
    /// the one written in configuration. The pipeline's total timeout is made
    /// the only deadline so that the answer to "how long can this call take"
    /// has one source.
    /// </remarks>
    public static IServiceCollection AddOutboundResilience(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = new OutboundResilienceOptions();
        configuration.GetSection(OutboundResilienceOptions.SectionName).Bind(options);
        options.Validate();
        services.AddSingleton(options);

        var metrics = new ResilienceMetrics();
        services.AddSingleton(metrics);

        var stateProvider = new CircuitBreakerStateProvider();
        services.AddSingleton(stateProvider);

        services.AddSingleton<UpstreamFaultSwitch>();

        services.AddHttpClient<IUpstreamClient, UpstreamClient>(client =>
        {
            client.BaseAddress = BuildBaseAddress(options.BaseAddress);
            client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        })
        .AddResilienceHandler(
            OutboundResiliencePipeline.PipelineName,
            builder => OutboundResiliencePipeline.Configure(builder, options, metrics, stateProvider));

        return services;
    }

    /// <summary>
    /// Guarantees the trailing slash a relative request URI needs.
    /// </summary>
    /// <remarks>
    /// Uri resolution replaces the last path segment when the base has no
    /// trailing slash, so a base of ".../api/upstream" and a request of
    /// "status" resolve to ".../api/status" - a real URL, a 404, and a
    /// configuration typo that looks like a routing bug. Normalising here
    /// means the configuration file can be written either way.
    /// </remarks>
    private static Uri BuildBaseAddress(string configured)
        => new(configured.EndsWith('/') ? configured : configured + "/", UriKind.Absolute);
}
