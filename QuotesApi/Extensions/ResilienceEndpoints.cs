using QuotesApi.Resilience;
using Polly.CircuitBreaker;

namespace QuotesApi.Extensions;

/// <summary>
/// The endpoints perf/breaker-timeline.ps1 drives Day 22's demonstration
/// through, plus the stub dependency it drives it against.
/// </summary>
/// <remarks>
/// Anonymous and ungated, on the same terms as Day 21's cache diagnostics: they
/// exist so the exercise is reproducible with one command, they expose counters
/// and a fault switch and nothing else, and the fault switch in particular is a
/// write anybody can call. In anything real these would sit behind an
/// environment check or an admin policy, and the stub upstream would not be
/// deployed at all.
/// </remarks>
public static class ResilienceEndpoints
{
    /// <summary>
    /// The stand-in for the outbound dependency. Its whole job is to fail when
    /// told to, and to count how often it was actually reached.
    /// </summary>
    public static IEndpointRouteBuilder MapUpstreamStubEndpoints(this IEndpointRouteBuilder app)
    {
        var upstream = app.MapGroup("/api/upstream");

        upstream.MapGet("/status", async (UpstreamFaultSwitch faults, CancellationToken ct) =>
        {
            faults.RecordRequest();
            return await RespondAsync(faults, ct);
        })
        .WithName("UpstreamStatus")
        .AllowAnonymous();

        upstream.MapPost("/submit", async (UpstreamFaultSwitch faults, CancellationToken ct) =>
        {
            faults.RecordRequest();
            return await RespondAsync(faults, ct);
        })
        .WithName("UpstreamSubmit")
        .AllowAnonymous();

        upstream.MapPost("/mode/{mode}", (
            string mode, double? slowDelaySeconds, UpstreamFaultSwitch faults) =>
        {
            if (!Enum.TryParse<UpstreamMode>(mode, ignoreCase: true, out var parsed))
            {
                return Results.BadRequest(new
                {
                    error = $"Unknown mode '{mode}'.",
                    allowed = Enum.GetNames<UpstreamMode>(),
                });
            }

            // slowDelaySeconds is what lets one mode play two different roles.
            // Longer than the attempt timeout, Slow is an unresponsive
            // dependency and exercises the timeout path. Shorter than it, Slow
            // is a merely busy dependency that still succeeds - which is what
            // the bulkhead needs, because a bulkhead is only observable while
            // calls are occupying permits without failing.
            if (slowDelaySeconds is { } seconds)
            {
                if (seconds is < 0 or > 60)
                {
                    return Results.BadRequest(new
                    {
                        error = $"slowDelaySeconds must be between 0 and 60. Got {seconds}.",
                    });
                }

                faults.SlowDelay = TimeSpan.FromSeconds(seconds);
            }

            faults.Mode = parsed;

            return Results.Ok(new
            {
                mode = parsed.ToString(),
                slowDelaySeconds = faults.SlowDelay.TotalSeconds,
                requests = faults.Requests,
            });
        })
        .WithName("SetUpstreamMode")
        .AllowAnonymous();

        upstream.MapGet("/state", (UpstreamFaultSwitch faults) => Results.Ok(new
        {
            mode = faults.Mode.ToString(),
            requests = faults.Requests,
            slowDelaySeconds = faults.SlowDelay.TotalSeconds,
        }))
        .WithName("UpstreamState")
        .AllowAnonymous();

        upstream.MapPost("/reset", (UpstreamFaultSwitch faults) =>
        {
            // The mode is deliberately left alone. Resetting the counter and
            // the fault state together would make it impossible to start a
            // measurement window part-way through an outage, which is exactly
            // what the "did the dependency stop being called" check needs.
            faults.ResetRequests();
            return Results.NoContent();
        })
        .WithName("ResetUpstreamCounter")
        .AllowAnonymous();

        return app;
    }

    /// <summary>
    /// The caller's side: two endpoints that go through the pipeline, and the
    /// counters that say what the pipeline did.
    /// </summary>
    public static IEndpointRouteBuilder MapResilienceDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        var resilience = app.MapGroup("/api/resilience");

        // GET, so the pipeline is allowed to retry it.
        resilience.MapGet("/call", async (
            IUpstreamClient client, HttpResponse response, CancellationToken ct) =>
        {
            var result = await client.GetStatusAsync(ct);
            return ToHttpResult(result, response);
        })
        .WithName("ResilientGet")
        .AllowAnonymous();

        // POST, so the pipeline will only retry it when the caller supplies a
        // key that says a repeat is safe. Same pipeline, same dependency; the
        // only difference is what the request claims about itself.
        resilience.MapPost("/call", async (
            IUpstreamClient client, HttpResponse response, string? idempotencyKey, CancellationToken ct) =>
        {
            var result = await client.SubmitAsync(idempotencyKey, ct);
            return ToHttpResult(result, response);
        })
        .WithName("ResilientPost")
        .AllowAnonymous();

        resilience.MapGet("/stats", (
            ResilienceMetrics metrics,
            CircuitBreakerStateProvider breaker,
            UpstreamFaultSwitch faults,
            OutboundResilienceOptions options) =>
        {
            var snapshot = metrics.Snapshot();

            return Results.Ok(new
            {
                circuitState = breaker.CircuitState.ToString(),
                upstreamMode = faults.Mode.ToString(),
                upstreamRequestsReceived = faults.Requests,
                calls = snapshot.Calls,
                successes = snapshot.Successes,
                upstreamFailures = snapshot.UpstreamFailures,
                shortCircuits = snapshot.ShortCircuits,
                bulkheadRejections = snapshot.BulkheadRejections,
                retries = snapshot.Retries,
                retriesSuppressedAsNonIdempotent = snapshot.RetriesSuppressedAsNonIdempotent,
                attemptTimeouts = snapshot.AttemptTimeouts,
                totalTimeouts = snapshot.TotalTimeouts,
                breakerOpened = snapshot.BreakerOpened,
                breakerHalfOpened = snapshot.BreakerHalfOpened,
                breakerClosed = snapshot.BreakerClosed,
                configuration = new
                {
                    maxConcurrentCalls = options.MaxConcurrentCalls,
                    queueLimit = options.QueueLimit,
                    totalRequestTimeout = options.TotalRequestTimeout.ToString(),
                    attemptTimeout = options.AttemptTimeout.ToString(),
                    maxRetryAttempts = options.MaxRetryAttempts,
                    failureRatio = options.FailureRatio,
                    samplingDuration = options.SamplingDuration.ToString(),
                    minimumThroughput = options.MinimumThroughput,
                    breakDuration = options.BreakDuration.ToString(),
                },
            });
        })
        .WithName("ResilienceStats")
        .AllowAnonymous();

        resilience.MapPost("/reset", (ResilienceMetrics metrics, UpstreamFaultSwitch faults) =>
        {
            // Counters on both sides of the call, so a run starts from zero on
            // the caller and on the dependency. The circuit's own state is not
            // reset, and cannot be from here: it is Polly's, it decays on its
            // own schedule, and a demo that could zero it would be able to hide
            // the thing it is supposed to be showing.
            metrics.Reset();
            faults.ResetRequests();
            return Results.NoContent();
        })
        .WithName("ResetResilienceStats")
        .AllowAnonymous();

        return app;
    }

    private static async Task<IResult> RespondAsync(UpstreamFaultSwitch faults, CancellationToken ct)
    {
        switch (faults.Mode)
        {
            case UpstreamMode.Failing:
                return Results.Json(
                    new { upstream = "failing" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            case UpstreamMode.Slow:
                await Task.Delay(faults.SlowDelay, ct);
                return Results.Ok(new { upstream = "slow-but-ok" });

            default:
                return Results.Ok(new { upstream = "ok" });
        }
    }

    /// <summary>
    /// Turns a pipeline outcome into the status code that describes it.
    /// </summary>
    /// <remarks>
    /// Worth doing properly rather than returning 503 for everything, because
    /// these mean different things to a caller deciding what to do next:
    ///
    /// <list type="bullet">
    /// <item><b>429</b> for a bulkhead rejection - this service is busy, the
    /// dependency may be perfectly healthy, come back shortly.</item>
    /// <item><b>503</b> for an open circuit - no Retry-After here, because
    /// Polly's BrokenCircuitException does not carry one (unlike the
    /// bulkhead's RateLimiterRejectedException, which does). GET
    /// /api/resilience/stats reports circuitState instead.</item>
    /// <item><b>504</b> for a timeout - the request may still be running
    /// somewhere, which is materially different from it never having
    /// started.</item>
    /// <item><b>502</b> for a dependency that answered badly or a connection
    /// that failed - the fault is downstream, not here.</item>
    /// </list>
    /// </remarks>
    private static IResult ToHttpResult(UpstreamCallResult result, HttpResponse response)
    {
        if (result.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero)
        {
            response.Headers["Retry-After"] =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }

        var statusCode = result.Outcome switch
        {
            UpstreamOutcome.Success => StatusCodes.Status200OK,
            UpstreamOutcome.BulkheadRejected => StatusCodes.Status429TooManyRequests,
            UpstreamOutcome.ShortCircuited => StatusCodes.Status503ServiceUnavailable,
            UpstreamOutcome.TimedOut => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status502BadGateway,
        };

        return Results.Json(
            new
            {
                outcome = result.Outcome.ToString(),
                upstreamStatusCode = result.StatusCode,
                elapsedMs = result.ElapsedMilliseconds,
                detail = result.Detail,
                retryAfterSeconds = result.RetryAfter?.TotalSeconds,
            },
            statusCode: statusCode);
    }
}
