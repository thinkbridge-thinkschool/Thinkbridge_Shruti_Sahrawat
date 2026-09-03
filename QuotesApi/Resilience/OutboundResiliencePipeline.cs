using System.Threading.RateLimiting;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Retry;
using Polly.Timeout;
using Serilog;

namespace QuotesApi.Resilience;

/// <summary>
/// Day 22's outbound resilience pipeline: bulkhead, total timeout, retry,
/// circuit breaker, attempt timeout - in that order, outermost first.
/// </summary>
/// <remarks>
/// <b>Why this is a static Configure rather than inline in the DI extension.</b>
/// The unit tests build a client over a stub HttpMessageHandler and call this
/// same method. A test that reimplemented the pipeline would prove that the
/// copy in the test file behaves correctly, which is not the claim anyone
/// cares about. One definition, two callers.
///
/// <b>Why this order.</b> It is the order
/// <c>AddStandardResilienceHandler</c> uses, and each position is load-bearing:
///
/// <list type="number">
/// <item><b>Bulkhead, outermost.</b> A rejection here costs nothing - no
/// socket, no timer, no retry budget. Putting it inside the retry would mean
/// each retry attempt queues for its own permit, so a saturated dependency
/// would be hit by exactly the traffic the bulkhead exists to withhold.</item>
///
/// <item><b>Total timeout, outside the retry.</b> This is the caller's actual
/// promise and the correction to Day 5, where a lone per-attempt timeout let a
/// request run 17.9 seconds under a "10-second timeout". A budget that does
/// not contain the retry loop is not a budget.</item>
///
/// <item><b>Retry, outside the breaker.</b> So that each attempt consults the
/// breaker. The consequence is worth stating plainly because it surprises
/// people: the breaker counts attempts, not calls, so one failing idempotent
/// call contributes four executions towards MinimumThroughput. A single caller
/// can therefore open the circuit on its own. That is intended - four failures
/// in a row against one dependency is evidence regardless of how many callers
/// produced them - but it does mean MinimumThroughput is not a count of
/// users.</item>
///
/// <item><b>Circuit breaker, outside the attempt timeout.</b> So a timed-out
/// attempt is counted as a failure. A dependency that never answers is failing
/// even though it never returned a status code, and a breaker that only
/// watched status codes would never open against the worst kind of
/// outage.</item>
///
/// <item><b>Attempt timeout, innermost.</b> It bounds one HTTP attempt and
/// nothing else.</item>
/// </list>
///
/// <b>Logging.</b> Serilog's static logger, matching how Program.cs already
/// logs from this kind of callback. The pipeline is constructed during service
/// registration, before any provider exists to resolve an ILogger from, and
/// threading a logger factory through purely to avoid a static would buy
/// nothing here. SourceContext is set explicitly so the demo's output can be
/// filtered to these lines.
/// </remarks>
public static class OutboundResiliencePipeline
{
    public const string PipelineName = "outbound";

    private static readonly Serilog.ILogger Logger =
        Log.ForContext("SourceContext", "QuotesApi.Resilience.OutboundResiliencePipeline");

    public static void Configure(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        OutboundResilienceOptions options,
        ResilienceMetrics metrics,
        CircuitBreakerStateProvider stateProvider)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(stateProvider);

        // 1. Bulkhead. Bounded concurrency against this one dependency, so a
        //    dependency that stops answering cannot hold every thread and
        //    connection in the process hostage.
        builder.AddRateLimiter(new RateLimiterStrategyOptions
        {
            DefaultRateLimiterOptions = new ConcurrencyLimiterOptions
            {
                PermitLimit = options.MaxConcurrentCalls,
                QueueLimit = options.QueueLimit,
            },
            OnRejected = _ =>
            {
                metrics.RecordBulkheadRejection();
                Logger.Warning(
                    "Bulkhead rejected a call: all {PermitLimit} permits in use, queue limit {QueueLimit}",
                    options.MaxConcurrentCalls,
                    options.QueueLimit);
                return default;
            },
        });

        // 2. Total timeout. The caller's budget, retries and backoff included.
        builder.AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = options.TotalRequestTimeout,
            OnTimeout = _ =>
            {
                metrics.RecordTotalTimeout();
                Logger.Warning(
                    "Total request budget of {TotalTimeout} exhausted - giving up on the whole operation",
                    options.TotalRequestTimeout);
                return default;
            },
        });

        // 3. Retry, gated on idempotency. See RetryEligibility for why the gate
        //    is on idempotency rather than the framework's safety-based helper.
        builder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = options.MaxRetryAttempts,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = options.RetryBaseDelay,
            ShouldHandle = args =>
            {
                if (!TransientHttpFailure.Matches(args.Outcome))
                {
                    return ValueTask.FromResult(false);
                }

                var request = CurrentRequest(args.Context);

                if (RetryEligibility.IsRetryable(request))
                {
                    return ValueTask.FromResult(true);
                }

                // The retry that deliberately did not happen. Counted, because
                // a gate that is working is indistinguishable from a gate that
                // was never wired up unless it leaves a trace.
                metrics.RecordRetrySuppressed();
                Logger.Warning(
                    "Not retrying {Method} {Uri} after a transient failure: the method is not "
                    + "idempotent and the request carries no {Header}. Repeating it could duplicate "
                    + "an effect the dependency has already applied",
                    request?.Method.Method ?? "(unknown method)",
                    request?.RequestUri?.PathAndQuery ?? "(unknown uri)",
                    RetryEligibility.IdempotencyKeyHeader);

                return ValueTask.FromResult(false);
            },
            OnRetry = args =>
            {
                metrics.RecordRetry();
                Logger.Warning(
                    "Retry {Attempt} in {Delay}ms after {Outcome}",
                    args.AttemptNumber + 1,
                    args.RetryDelay.TotalMilliseconds,
                    args.Outcome.Exception?.GetType().Name
                        ?? ((int?)args.Outcome.Result?.StatusCode)?.ToString()
                        ?? "an unknown outcome");
                return default;
            },
        });

        // 4. Circuit breaker. Same failure definition as the retry above.
        builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = options.FailureRatio,
            SamplingDuration = options.SamplingDuration,
            MinimumThroughput = options.MinimumThroughput,
            BreakDuration = options.BreakDuration,
            StateProvider = stateProvider,
            ShouldHandle = args => ValueTask.FromResult(TransientHttpFailure.Matches(args.Outcome)),
            OnOpened = args =>
            {
                metrics.RecordBreakerOpened();
                Logger.Error(
                    "Circuit OPENED for {BreakDuration}s - calls will fail fast without reaching the dependency",
                    args.BreakDuration.TotalSeconds);
                return default;
            },
            OnHalfOpened = _ =>
            {
                metrics.RecordBreakerHalfOpened();
                Logger.Warning(
                    "Circuit HALF-OPEN - letting a single probe through to see whether the dependency recovered");
                return default;
            },
            OnClosed = _ =>
            {
                metrics.RecordBreakerClosed();
                Logger.Information("Circuit CLOSED - the dependency answered, normal traffic resumes");
                return default;
            },
        });

        // 5. Attempt timeout. One attempt, nothing more.
        builder.AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = options.AttemptTimeout,
            OnTimeout = _ =>
            {
                metrics.RecordAttemptTimeout();
                Logger.Warning(
                    "Attempt abandoned after {AttemptTimeout} - this counts as a failure for the breaker",
                    options.AttemptTimeout);
                return default;
            },
        });
    }

    /// <summary>
    /// The request the pipeline is currently executing, or null if it cannot
    /// be determined.
    /// </summary>
    /// <remarks>
    /// Isolated into one method because it is the single place this pipeline
    /// reaches outside Polly's own abstractions.
    /// Microsoft.Extensions.Http.Resilience attaches the in-flight
    /// HttpRequestMessage to the resilience context, which is how the shipped
    /// DisableFor / DisableForUnsafeHttpMethods helpers make the same decision
    /// this pipeline makes for itself. Null is handled as "not safe to retry"
    /// by RetryEligibility rather than being treated as an error.
    /// </remarks>
    private static HttpRequestMessage? CurrentRequest(ResilienceContext context)
        => context.GetRequestMessage();
}
