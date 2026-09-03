using System.Diagnostics;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly.CircuitBreaker;
using QuotesApi.Resilience;

namespace Quotes.Tests.Unit;

/// <summary>
/// Day 22. Each of the four primitives, asserted against the real pipeline.
/// </summary>
/// <remarks>
/// Every test here builds its client through
/// <see cref="OutboundResiliencePipeline.Configure"/> - the same method
/// Program.cs registers - over a stub HttpMessageHandler. That matters more
/// than it looks: a test that assembled its own AddRetry/AddCircuitBreaker
/// chain would prove that the chain in the test file behaves correctly, which
/// is not a claim anyone needs. If the production pipeline loses its
/// idempotency gate, these tests fail.
///
/// No sockets and no real clock beyond a single 900ms wait in the recovery
/// test, which is unavoidable: Polly's circuit breaker will not accept a break
/// duration under 500ms, and the half-open transition is driven by wall time.
/// The whole file runs in about two seconds.
/// </remarks>
public class OutboundResilienceTests : IDisposable
{
    private readonly List<ServiceProvider> _providers = [];

    // ---------------------------------------------------------------- retry

    [Fact]
    public async Task AnIdempotentGet_IsRetriedUntilItSucceeds()
    {
        var upstream = new ScriptedUpstream((attempt, _) => Respond(
            attempt <= 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));

        var harness = Build(TestOptions(), upstream);

        var result = await harness.Client.GetStatusAsync(CancellationToken.None);

        result.Outcome.Should().Be(UpstreamOutcome.Success);
        upstream.Calls.Should().Be(3, "two 503s should be retried before the 200 succeeds");
        harness.Metrics.Snapshot().Retries.Should().Be(2);
    }

    [Fact]
    public async Task ABarePost_IsNotRetried()
    {
        var upstream = new ScriptedUpstream((_, _) => Respond(HttpStatusCode.ServiceUnavailable));

        var harness = Build(TestOptions(), upstream);

        var result = await harness.Client.SubmitAsync(idempotencyKey: null, CancellationToken.None);

        result.Outcome.Should().Be(UpstreamOutcome.UpstreamFailure);
        upstream.Calls.Should().Be(
            1,
            "a POST with no idempotency key may already have been applied by the dependency, so "
            + "repeating it risks duplicating the effect");

        var snapshot = harness.Metrics.Snapshot();
        snapshot.Retries.Should().Be(0);
        snapshot.RetriesSuppressedAsNonIdempotent.Should().Be(
            1, "the retry that did not happen is the only evidence the gate is closed");
    }

    [Fact]
    public async Task APostCarryingAnIdempotencyKey_IsRetried()
    {
        var upstream = new ScriptedUpstream((attempt, _) => Respond(
            attempt <= 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));

        var harness = Build(TestOptions(), upstream);

        var result = await harness.Client.SubmitAsync(
            idempotencyKey: "day22-" + Guid.NewGuid().ToString("N"), CancellationToken.None);

        result.Outcome.Should().Be(UpstreamOutcome.Success);
        upstream.Calls.Should().Be(
            3,
            "the key is the caller stating that the dependency deduplicates repeats - the same "
            + "contract Day 20's outbox relies on");
        harness.Metrics.Snapshot().RetriesSuppressedAsNonIdempotent.Should().Be(0);
    }

    [Fact]
    public async Task ADelete_IsRetried_WhichTheFrameworkSafetyHelperWouldNotDo()
    {
        var upstream = new ScriptedUpstream((attempt, _) => Respond(
            attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));

        var harness = Build(TestOptions(), upstream);

        using var response = await harness.Http.DeleteAsync("thing/1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        upstream.Calls.Should().Be(
            2,
            "DELETE is idempotent - repeating it lands on the same end state - even though it is "
            + "not safe, which is the distinction DisableForUnsafeHttpMethods does not make");
    }

    [Fact]
    public async Task ANonTransientFailure_IsNotRetried()
    {
        var upstream = new ScriptedUpstream((_, _) => Respond(HttpStatusCode.BadRequest));

        var harness = Build(TestOptions(), upstream);

        var result = await harness.Client.GetStatusAsync(CancellationToken.None);

        result.Outcome.Should().Be(UpstreamOutcome.UpstreamFailure);
        result.StatusCode.Should().Be(400);
        upstream.Calls.Should().Be(
            1, "a 400 is the dependency saying the request is wrong; sending it again keeps it wrong");
        harness.Metrics.Snapshot().Retries.Should().Be(0);
    }

    // ------------------------------------------------------- circuit breaker

    [Fact]
    public async Task TheCircuit_OpensUnderSustainedFailure_StopsCallingTheDependency_AndRecovers()
    {
        var faults = new UpstreamFaultSwitch { Mode = UpstreamMode.Failing };

        var upstream = new ScriptedUpstream((_, _) => Respond(
            faults.Mode == UpstreamMode.Failing
                ? HttpStatusCode.ServiceUnavailable
                : HttpStatusCode.OK));

        var harness = Build(
            TestOptions(options =>
            {
                // 2 attempts per call, and 2 failed executions is enough
                // evidence - so one failing call trips it. The breaker counts
                // attempts, not calls, because it sits inside the retry.
                options.MaxRetryAttempts = 1;
                options.MinimumThroughput = 2;
                options.FailureRatio = 0.5;
                options.BreakDuration = TimeSpan.FromMilliseconds(500);
            }),
            upstream);

        // Closed -> Open.
        var duringOutage = await harness.Client.GetStatusAsync(CancellationToken.None);

        duringOutage.Outcome.Should().Be(UpstreamOutcome.UpstreamFailure);
        harness.Breaker.CircuitState.Should().Be(CircuitState.Open);
        upstream.Calls.Should().Be(2, "one call, retried once");

        var callsWhenTheCircuitOpened = upstream.Calls;

        // Open: refused locally, and - the part that matters - not forwarded.
        var whileOpen = await harness.Client.GetStatusAsync(CancellationToken.None);

        whileOpen.Outcome.Should().Be(UpstreamOutcome.ShortCircuited);
        upstream.Calls.Should().Be(
            callsWhenTheCircuitOpened,
            "an open circuit is only worth anything if the struggling dependency stops being "
            + "asked - failing the caller faster is the side effect, not the point");

        // Open -> HalfOpen -> Closed, once the dependency is healthy again.
        await Task.Delay(TimeSpan.FromMilliseconds(900));
        faults.Mode = UpstreamMode.Healthy;

        var afterRecovery = await harness.Client.GetStatusAsync(CancellationToken.None);

        afterRecovery.Outcome.Should().Be(UpstreamOutcome.Success);
        harness.Breaker.CircuitState.Should().Be(CircuitState.Closed);
        upstream.Calls.Should().Be(
            callsWhenTheCircuitOpened + 1, "recovery is proven by a single probe, not by a burst");

        var snapshot = harness.Metrics.Snapshot();
        snapshot.BreakerOpened.Should().Be(1);
        snapshot.BreakerHalfOpened.Should().BeGreaterThanOrEqualTo(1);
        snapshot.BreakerClosed.Should().Be(1);
        snapshot.ShortCircuits.Should().Be(1);
    }

    // -------------------------------------------------------------- bulkhead

    [Fact]
    public async Task TheBulkhead_RejectsACallWhenEveryPermitIsInUse()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var upstream = new ScriptedUpstream(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var harness = Build(
            TestOptions(options =>
            {
                options.MaxConcurrentCalls = 1;
                options.QueueLimit = 0;
            }),
            upstream);

        var held = harness.Client.GetStatusAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var rejected = await harness.Client.GetStatusAsync(CancellationToken.None);

        rejected.Outcome.Should().Be(UpstreamOutcome.BulkheadRejected);
        upstream.Calls.Should().Be(
            1,
            "the rejected call never reached the dependency - that is the difference between a "
            + "bulkhead and a queue");
        harness.Metrics.Snapshot().BulkheadRejections.Should().Be(1);

        release.SetResult();
        (await held).Outcome.Should().Be(
            UpstreamOutcome.Success, "the in-flight call is unaffected by the rejection behind it");
    }

    // -------------------------------------------------------------- timeouts

    [Fact]
    public async Task TheTotalBudget_EndsTheCall_EvenWhenNoSingleAttemptTimesOut()
    {
        // The Day 5 gap, restated: a timeout that only ever watches one
        // attempt lets the *operation* run far longer than the number in
        // its name, because nothing is watching the sum. An attempt timeout
        // that is not strictly smaller than the total budget is rejected by
        // OutboundResilienceOptions.Validate() (it could never fire first,
        // which is a misconfiguration, not a scenario to test), so this
        // cannot be proven by making the attempt timeout too large to fire.
        // Instead, the attempt timeout is real and comfortably larger than
        // any single attempt here takes (300ms against ~50ms of actual
        // work) - no individual attempt ever comes close to it - and it is
        // the *accumulation* of many such attempts, each one a fast,
        // ordinary retry, that the total budget has to catch. There is no
        // millisecond tie for a slow runner to win here: with roughly
        // fourteen attempts expected inside the 700ms budget, the total
        // timeout firing mid-flight during *some* attempt is the only
        // possible outcome, whichever attempt that turns out to be.
        var upstream = new ScriptedUpstream(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        var harness = Build(
            TestOptions(options =>
            {
                options.AttemptTimeout = TimeSpan.FromMilliseconds(300);
                options.TotalRequestTimeout = TimeSpan.FromMilliseconds(700);
                options.MaxRetryAttempts = 100;
                options.RetryBaseDelay = TimeSpan.Zero;
            }),
            upstream);

        var stopwatch = Stopwatch.StartNew();
        var result = await harness.Client.GetStatusAsync(CancellationToken.None);
        stopwatch.Stop();

        result.Outcome.Should().Be(UpstreamOutcome.TimedOut);
        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(5),
            "the total budget has to bound the call, or it is not a budget");

        var snapshot = harness.Metrics.Snapshot();
        snapshot.TotalTimeouts.Should().Be(
            1, "the total budget is what ended this call");
        snapshot.AttemptTimeouts.Should().Be(
            0, "every attempt finished in about 50ms, nowhere near its own 300ms timeout - only the accumulation of retries exhausted the total budget");
        upstream.Calls.Should().BeGreaterThanOrEqualTo(
            2, "the total budget should have allowed more than one attempt before it ended the call");
    }

    [Fact]
    public async Task TheTotalBudget_LeavesRoomForMoreThanOneAttempt()
    {
        // The companion proof: the total budget does not mean "one attempt
        // only" - it means attempts keep happening, each bounded by its own
        // attempt timeout, until either one succeeds or the budget runs
        // out. This is asserted on the thing that is actually true
        // regardless of runner speed - how many attempts were made - rather
        // than on which Polly strategy claims credit for stopping the
        // operation, which is what made the previous version of this test
        // flaky on a contended CI box. The total budget here (10s) is
        // ~25x the four attempts it needs to allow (4 x 100ms = 400ms), so
        // there is no realistic amount of scheduler jitter that turns this
        // into a race either.
        var upstream = new ScriptedUpstream(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var harness = Build(
            TestOptions(options =>
            {
                options.AttemptTimeout = TimeSpan.FromMilliseconds(100);
                options.TotalRequestTimeout = TimeSpan.FromSeconds(10);
                options.MaxRetryAttempts = 3;
                options.RetryBaseDelay = TimeSpan.Zero;
            }),
            upstream);

        var stopwatch = Stopwatch.StartNew();
        var result = await harness.Client.GetStatusAsync(CancellationToken.None);
        stopwatch.Stop();

        result.Outcome.Should().Be(UpstreamOutcome.TimedOut);
        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(5),
            "the total budget is ten seconds, but the four attempts should exhaust in well under one");

        var snapshot = harness.Metrics.Snapshot();
        upstream.Calls.Should().Be(
            4, "one initial attempt plus three retries, each abandoned at its own attempt timeout");
        snapshot.AttemptTimeouts.Should().Be(
            4, "every attempt individually timed out - none of them ever reached the dependency's five-second delay");
        snapshot.TotalTimeouts.Should().Be(
            0, "four attempts of 100ms each is nowhere near the ten-second total budget");
    }

    [Fact]
    public void Configuration_IsRejectedWhenTheAttemptTimeoutIsNotSmallerThanTheTotal()
    {
        var options = new OutboundResilienceOptions
        {
            AttemptTimeout = TimeSpan.FromSeconds(10),
            TotalRequestTimeout = TimeSpan.FromSeconds(10),
        };

        Action validate = options.Validate;

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*strictly less than*");
    }

    // ------------------------------------------------------------- machinery

    private static Task<HttpResponseMessage> Respond(HttpStatusCode statusCode)
        => Task.FromResult(new HttpResponseMessage(statusCode));

    /// <summary>
    /// Defaults chosen so that only the strategy under test can interfere:
    /// generous timeouts, no backoff delay, and a throughput floor high enough
    /// that the breaker stays out of the way unless a test lowers it.
    /// </summary>
    private static OutboundResilienceOptions TestOptions(
        Action<OutboundResilienceOptions>? configure = null)
    {
        var options = new OutboundResilienceOptions
        {
            BaseAddress = "http://upstream.test/",
            MaxConcurrentCalls = 16,
            QueueLimit = 0,
            TotalRequestTimeout = TimeSpan.FromSeconds(30),
            AttemptTimeout = TimeSpan.FromSeconds(10),
            MaxRetryAttempts = 3,
            RetryBaseDelay = TimeSpan.Zero,
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 1000,
            BreakDuration = TimeSpan.FromMilliseconds(500),
        };

        configure?.Invoke(options);
        options.Validate();
        return options;
    }

    private Harness Build(OutboundResilienceOptions options, ScriptedUpstream upstream)
    {
        var metrics = new ResilienceMetrics();
        var breaker = new CircuitBreakerStateProvider();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("upstream", client =>
            {
                client.BaseAddress = new Uri(options.BaseAddress);
                client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => upstream)
            .AddResilienceHandler(
                OutboundResiliencePipeline.PipelineName,
                builder => OutboundResiliencePipeline.Configure(builder, options, metrics, breaker));

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("upstream");

        return new Harness(new UpstreamClient(http, metrics), http, metrics, breaker);
    }

    private sealed record Harness(
        UpstreamClient Client,
        HttpClient Http,
        ResilienceMetrics Metrics,
        CircuitBreakerStateProvider Breaker);

    /// <summary>
    /// The dependency, scripted by attempt number. Counts every attempt that
    /// reached it, which is how the tests tell "the caller failed" apart from
    /// "the dependency was spared".
    /// </summary>
    private sealed class ScriptedUpstream(
        Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(Interlocked.Increment(ref _calls), cancellationToken);
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
