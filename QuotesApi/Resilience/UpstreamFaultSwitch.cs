namespace QuotesApi.Resilience;

public enum UpstreamMode
{
    /// <summary>Answers 200 immediately.</summary>
    Healthy,

    /// <summary>Answers 503 immediately.</summary>
    Failing,

    /// <summary>Answers 200, but only after a delay longer than the attempt timeout.</summary>
    Slow,
}

/// <summary>
/// The knob that makes the outbound dependency fail on demand, and the counter
/// that records how many requests actually reached it.
/// </summary>
/// <remarks>
/// A circuit breaker cannot be demonstrated against a dependency that works.
/// Proving the full lifecycle needs failure to start on command and stop on
/// command, which no real third party offers, so the dependency here is a stub
/// this process controls. What that does and does not prove is worth being
/// exact about: the pipeline is real, the sockets are real, the handler chain
/// is real, and the pipeline has no idea it is talking to a stub. Only the
/// failures are synthetic.
///
/// <see cref="Requests"/> is the counter that makes the breaker's value
/// measurable rather than asserted. While the circuit is open this number must
/// stop moving - if it keeps climbing, calls are still reaching the dependency
/// and the breaker is not protecting anything. It is the difference between
/// "the caller got errors faster" and "the struggling dependency stopped being
/// asked", and only the second one is what a breaker is for.
/// </remarks>
public sealed class UpstreamFaultSwitch
{
    private int _mode = (int)UpstreamMode.Healthy;
    private long _requests;

    public UpstreamMode Mode
    {
        get => (UpstreamMode)Volatile.Read(ref _mode);
        set => Volatile.Write(ref _mode, (int)value);
    }

    /// <summary>
    /// How long <see cref="UpstreamMode.Slow"/> takes to answer. Longer than
    /// the default attempt timeout on purpose, so that Slow exercises the
    /// timeout path rather than merely being sluggish.
    /// </summary>
    public TimeSpan SlowDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Requests that actually reached the dependency.</summary>
    public long Requests => Interlocked.Read(ref _requests);

    public void RecordRequest() => Interlocked.Increment(ref _requests);

    public void ResetRequests() => Interlocked.Exchange(ref _requests, 0);
}
