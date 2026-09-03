using System.Diagnostics;
using System.Net.Http.Json;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace QuotesApi.Resilience;

/// <summary>
/// How a call to the outbound dependency ended, from the caller's point of
/// view.
/// </summary>
/// <remarks>
/// Deliberately not a bool and not an exception. Every one of these means "the
/// call did not succeed", but they mean completely different things to whoever
/// is deciding what to tell the user, and collapsing them into one failure
/// type throws away the only information the pipeline produced.
/// </remarks>
public enum UpstreamOutcome
{
    /// <summary>The dependency answered successfully.</summary>
    Success,

    /// <summary>The dependency answered, and the answer was a failure.</summary>
    UpstreamFailure,

    /// <summary>The circuit was open. The call never left this process.</summary>
    ShortCircuited,

    /// <summary>The bulkhead was full. The call never left this process.</summary>
    BulkheadRejected,

    /// <summary>A timeout fired - either one attempt's or the whole budget's.</summary>
    TimedOut,

    /// <summary>DNS, connection refused, connection reset - the request may never have arrived.</summary>
    TransportFailure,
}

/// <param name="RetryAfter">
/// Only set when the pipeline knows something concrete about when to try
/// again. Today that is only the bulkhead: Polly's RateLimiterRejectedException
/// carries a RetryAfter, but its BrokenCircuitException does not - the state
/// (open/half-open/closed) is what the breaker exposes instead.
/// </param>
public sealed record UpstreamCallResult(
    UpstreamOutcome Outcome,
    int? StatusCode,
    long ElapsedMilliseconds,
    string? Detail,
    TimeSpan? RetryAfter = null);

public interface IUpstreamClient
{
    /// <summary>A GET, so the pipeline will retry it.</summary>
    Task<UpstreamCallResult> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// A POST, so the pipeline will only retry it when
    /// <paramref name="idempotencyKey"/> is supplied.
    /// </summary>
    Task<UpstreamCallResult> SubmitAsync(string? idempotencyKey, CancellationToken cancellationToken);
}

/// <summary>
/// The one thing in this codebase that talks to the outbound dependency.
/// </summary>
/// <remarks>
/// A typed client rather than a raw IHttpClientFactory lookup by string, for
/// the reason that matters here: the resilience pipeline is attached to a named
/// client, so anything that can ask for an HttpClient by a different name can
/// bypass the pipeline entirely and nothing will fail. Making this the only
/// door means the bulkhead's permit count is a real bound on concurrency
/// against the dependency and not a bound on one of several ways to reach it.
///
/// The exceptions Polly throws are translated into UpstreamCallResult here
/// rather than left to bubble. Every caller would otherwise need to know
/// Polly's exception types to tell "the dependency is broken" from "we chose
/// not to call it", and that knowledge does not belong in a controller.
/// </remarks>
public sealed class UpstreamClient(HttpClient http, ResilienceMetrics metrics) : IUpstreamClient
{
    public Task<UpstreamCallResult> GetStatusAsync(CancellationToken cancellationToken)
        => SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "status"), cancellationToken);

    public Task<UpstreamCallResult> SubmitAsync(string? idempotencyKey, CancellationToken cancellationToken)
        => SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "submit")
                {
                    Content = JsonContent.Create(new { submittedAt = DateTimeOffset.UtcNow }),
                };

                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    request.Headers.TryAddWithoutValidation(
                        RetryEligibility.IdempotencyKeyHeader, idempotencyKey);
                }

                return request;
            },
            cancellationToken);

    private async Task<UpstreamCallResult> SendAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        metrics.RecordCall();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = requestFactory();
            using var response = await http.SendAsync(request, cancellationToken);

            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                metrics.RecordSuccess();
                return new UpstreamCallResult(
                    UpstreamOutcome.Success,
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    Detail: null);
            }

            metrics.RecordUpstreamFailure();
            return new UpstreamCallResult(
                UpstreamOutcome.UpstreamFailure,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                Detail: $"The dependency answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();

            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                // The caller gave up. That is not a failure of the dependency
                // and not this method's to report as one.
                throw;
            }

            return Classify(exception, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Turns whatever came out of the pipeline into an outcome.
    /// </summary>
    /// <remarks>
    /// Walks the inner-exception chain rather than matching only the outermost
    /// type. HttpClient is entitled to wrap an exception thrown by a delegating
    /// handler, and Polly's rejections are thrown from inside one - so a
    /// catch clause keyed on the outermost type is correct until the day the
    /// runtime decides to wrap, at which point every rejection silently
    /// reclassifies as a transport failure and the metrics quietly stop
    /// meaning anything. Walking the chain is right either way.
    /// </remarks>
    private UpstreamCallResult Classify(Exception exception, long elapsedMilliseconds)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case BrokenCircuitException:
                    // Not a failure of the dependency - a decision not to call
                    // it. The dependency was not touched, and that is the point.
                    //
                    // No RetryAfter here: unlike RateLimiterRejectedException,
                    // Polly's BrokenCircuitException does not carry one - the
                    // breaker's exception says the circuit is open, not when it
                    // will next allow a probe. GET /api/resilience/stats
                    // exposes circuitState instead, which is the same fact read
                    // from the same CircuitBreakerStateProvider Program.cs
                    // registers.
                    metrics.RecordShortCircuit();
                    return new UpstreamCallResult(
                        UpstreamOutcome.ShortCircuited,
                        StatusCode: null,
                        elapsedMilliseconds,
                        Detail: "The circuit is open; the dependency was not called.");

                case RateLimiterRejectedException rejected:
                    // Already counted by the pipeline's OnRejected callback;
                    // counting it again here would double every rejection.
                    return new UpstreamCallResult(
                        UpstreamOutcome.BulkheadRejected,
                        StatusCode: null,
                        elapsedMilliseconds,
                        Detail: "Too many calls to this dependency are already in flight.",
                        RetryAfter: rejected.RetryAfter);

                case TimeoutRejectedException:
                    metrics.RecordUpstreamFailure();
                    return new UpstreamCallResult(
                        UpstreamOutcome.TimedOut,
                        StatusCode: null,
                        elapsedMilliseconds,
                        Detail: "The dependency did not answer within the budget.");

                case OperationCanceledException:
                    // A timeout that surfaced as cancellation rather than as
                    // TimeoutRejectedException. The caller's own token was
                    // already ruled out above.
                    metrics.RecordUpstreamFailure();
                    return new UpstreamCallResult(
                        UpstreamOutcome.TimedOut,
                        StatusCode: null,
                        elapsedMilliseconds,
                        Detail: "The call was abandoned on a timeout.");
            }
        }

        metrics.RecordUpstreamFailure();
        return new UpstreamCallResult(
            UpstreamOutcome.TransportFailure,
            StatusCode: null,
            elapsedMilliseconds,
            Detail: exception.Message);
    }
}
