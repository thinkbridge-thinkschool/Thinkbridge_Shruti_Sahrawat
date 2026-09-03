namespace QuotesApi.Resilience;

/// <summary>
/// The counters Day 22's evidence rests on. Process-wide, reset between demo
/// runs through POST /api/resilience/reset.
/// </summary>
/// <remarks>
/// A resilience pipeline is mostly invisible: it succeeds by turning failures
/// into other failures, or into nothing at all. These counters exist so that
/// each strategy can be shown to have actually fired, rather than asserted to
/// have been configured.
///
/// The distinction that matters most is <see cref="RecordShortCircuit"/>
/// against <see cref="RecordUpstreamFailure"/>. Both are failures from the
/// caller's point of view, and the whole value of a circuit breaker is that
/// the second stops happening and the first starts - fast, local, and without
/// touching the dependency at all. A run where those two numbers do not swap
/// over is a run where the breaker did not do anything.
///
/// <see cref="RecordRetrySuppressed"/> is the counter for the retry that
/// deliberately did not happen: a transient failure on a request that was not
/// safe to repeat. It is the only positive evidence that the idempotency gate
/// is closed, because a gate that works looks exactly like a gate that is not
/// there.
/// </remarks>
public sealed class ResilienceMetrics
{
    private long _calls;
    private long _successes;
    private long _retries;
    private long _retriesSuppressed;
    private long _attemptTimeouts;
    private long _totalTimeouts;
    private long _bulkheadRejections;
    private long _shortCircuits;
    private long _upstreamFailures;
    private long _breakerOpened;
    private long _breakerHalfOpened;
    private long _breakerClosed;

    /// <summary>One per operation the typed client starts.</summary>
    public void RecordCall() => Interlocked.Increment(ref _calls);

    public void RecordSuccess() => Interlocked.Increment(ref _successes);

    /// <summary>One per retry actually taken.</summary>
    public void RecordRetry() => Interlocked.Increment(ref _retries);

    /// <summary>One per transient failure the idempotency gate refused to retry.</summary>
    public void RecordRetrySuppressed() => Interlocked.Increment(ref _retriesSuppressed);

    public void RecordAttemptTimeout() => Interlocked.Increment(ref _attemptTimeouts);

    public void RecordTotalTimeout() => Interlocked.Increment(ref _totalTimeouts);

    public void RecordBulkheadRejection() => Interlocked.Increment(ref _bulkheadRejections);

    /// <summary>One per call the open circuit refused without leaving the process.</summary>
    public void RecordShortCircuit() => Interlocked.Increment(ref _shortCircuits);

    /// <summary>One per call that reached the dependency and came back a failure.</summary>
    public void RecordUpstreamFailure() => Interlocked.Increment(ref _upstreamFailures);

    public void RecordBreakerOpened() => Interlocked.Increment(ref _breakerOpened);

    public void RecordBreakerHalfOpened() => Interlocked.Increment(ref _breakerHalfOpened);

    public void RecordBreakerClosed() => Interlocked.Increment(ref _breakerClosed);

    /// <summary>
    /// Reads every counter once, for the same reason CacheMetrics does: a
    /// snapshot whose fields were sampled at different instants can report
    /// totals that disagree with their own parts.
    /// </summary>
    public ResilienceMetricsSnapshot Snapshot() => new(
        Calls: Interlocked.Read(ref _calls),
        Successes: Interlocked.Read(ref _successes),
        Retries: Interlocked.Read(ref _retries),
        RetriesSuppressedAsNonIdempotent: Interlocked.Read(ref _retriesSuppressed),
        AttemptTimeouts: Interlocked.Read(ref _attemptTimeouts),
        TotalTimeouts: Interlocked.Read(ref _totalTimeouts),
        BulkheadRejections: Interlocked.Read(ref _bulkheadRejections),
        ShortCircuits: Interlocked.Read(ref _shortCircuits),
        UpstreamFailures: Interlocked.Read(ref _upstreamFailures),
        BreakerOpened: Interlocked.Read(ref _breakerOpened),
        BreakerHalfOpened: Interlocked.Read(ref _breakerHalfOpened),
        BreakerClosed: Interlocked.Read(ref _breakerClosed));

    public void Reset()
    {
        Interlocked.Exchange(ref _calls, 0);
        Interlocked.Exchange(ref _successes, 0);
        Interlocked.Exchange(ref _retries, 0);
        Interlocked.Exchange(ref _retriesSuppressed, 0);
        Interlocked.Exchange(ref _attemptTimeouts, 0);
        Interlocked.Exchange(ref _totalTimeouts, 0);
        Interlocked.Exchange(ref _bulkheadRejections, 0);
        Interlocked.Exchange(ref _shortCircuits, 0);
        Interlocked.Exchange(ref _upstreamFailures, 0);
        Interlocked.Exchange(ref _breakerOpened, 0);
        Interlocked.Exchange(ref _breakerHalfOpened, 0);
        Interlocked.Exchange(ref _breakerClosed, 0);
    }
}

public sealed record ResilienceMetricsSnapshot(
    long Calls,
    long Successes,
    long Retries,
    long RetriesSuppressedAsNonIdempotent,
    long AttemptTimeouts,
    long TotalTimeouts,
    long BulkheadRejections,
    long ShortCircuits,
    long UpstreamFailures,
    long BreakerOpened,
    long BreakerHalfOpened,
    long BreakerClosed);
