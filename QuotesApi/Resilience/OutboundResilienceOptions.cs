namespace QuotesApi.Resilience;

/// <summary>
/// How Day 22's outbound resilience pipeline is configured. Bound from the
/// "Resilience" section.
/// </summary>
/// <remarks>
/// Every number here is a policy decision, not a tuning knob, and the two that
/// matter most are the two timeouts.
///
/// Day 5 wired a single <c>AddTimeout(10s)</c> and the write-up recorded a
/// request that took 17.9 seconds anyway. That was not a bug in Polly. A
/// timeout strategy placed inside the retry loop bounds one attempt, and four
/// attempts plus their backoff can exceed it without any single attempt ever
/// crossing the line. The fix is two timeouts, not a bigger one:
/// <see cref="AttemptTimeout"/> sits innermost and decides when to give up on
/// a single attempt, and <see cref="TotalRequestTimeout"/> sits outside the
/// retry and is what the caller is actually promised.
///
/// So <see cref="AttemptTimeout"/> must be strictly smaller than
/// <see cref="TotalRequestTimeout"/> - otherwise the inner timeout can never
/// fire before the outer one does, and the retry loop becomes decorative.
/// <see cref="Validate"/> refuses to start rather than let that combination
/// through, on the same reasoning as the Jwt:Key check in Program.cs: a server
/// that will not start is a five-minute problem, and a server quietly running
/// the wrong policy is a problem nobody notices.
///
/// The defaults deliberately allow the total budget to be the binding
/// constraint: 4 attempts x 2s plus jittered backoff can exceed 8s, so under a
/// dead dependency the caller waits ~8s and not ~9s. That is the point.
/// </remarks>
public sealed class OutboundResilienceOptions
{
    public const string SectionName = "Resilience";

    /// <summary>
    /// Where the outbound dependency lives. Points at this host's own stub
    /// upstream in development - see Extensions/ResilienceDiagnosticsEndpoints.cs
    /// for why the dependency is a stub and what that does and does not prove.
    /// </summary>
    public string BaseAddress { get; set; } = "http://localhost:5067/api/upstream/";

    // --- Bulkhead -----------------------------------------------------------

    /// <summary>
    /// How many calls may be in flight against this dependency at once. This
    /// is the bulkhead: the cap exists so that one slow dependency cannot
    /// consume every thread and connection this process has and take the rest
    /// of the API down with it. Bounded concurrency is the difference between
    /// one degraded endpoint and one degraded service.
    /// </summary>
    public int MaxConcurrentCalls { get; set; } = 8;

    /// <summary>
    /// How many callers may wait for a permit. Zero on purpose: a queue in
    /// front of a saturated dependency converts a fast rejection into a slow
    /// one, and the caller has a total timeout to honour either way. Rejecting
    /// immediately gives the caller a 429 it can act on now.
    /// </summary>
    public int QueueLimit { get; set; }

    // --- Timeouts -----------------------------------------------------------

    /// <summary>The whole operation's budget, retries and backoff included.</summary>
    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>One attempt's budget. Must be smaller than the total.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(2);

    // --- Retry --------------------------------------------------------------

    /// <summary>
    /// Retries after the first attempt, so 3 means up to 4 attempts. Polly's
    /// own floor is 1 - it has no concept of a retry strategy that never
    /// retries - so switching retries off is a matter of not adding the
    /// strategy, not of setting this to zero.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// First backoff delay; doubled per attempt and jittered. Jitter is not
    /// cosmetic - it is what stops every client that saw the same outage
    /// retrying in the same millisecond and turning a recovery into a second
    /// outage.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    // --- Circuit breaker ----------------------------------------------------

    /// <summary>Proportion of handled failures in the window that opens the circuit.</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>The rolling window the ratio is measured over. Polly requires >= 500ms.</summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Minimum executions in the window before the ratio is allowed to open
    /// the circuit. Polly requires >= 2. Worth being precise about what counts:
    /// the breaker sits inside the retry, so it sees attempts, not calls. With
    /// <see cref="MaxRetryAttempts"/> at 3, one failing idempotent call already
    /// contributes 4 executions - which means a throughput floor of 20 is five
    /// failing calls, not twenty.
    ///
    /// Set well above what a handful of incidental failures elsewhere could
    /// reach: perf/breaker-timeline.ps1 deliberately makes a few calls fail
    /// earlier in the run to prove the retry gate (see its phase 2), and those
    /// executions land in this same rolling window because the breaker has no
    /// notion of "phases" - only of attempts. A floor tuned for an isolated
    /// failure would trip on that incidental noise before the dedicated
    /// sustained-failure phase ever runs, which is exactly what happened
    /// during this exercise's first pass at the demo script.
    /// </summary>
    public int MinimumThroughput { get; set; } = 20;

    /// <summary>
    /// How long the circuit stays open before it allows one probe through.
    /// Polly requires >= 500ms. Five seconds is short for production - it is
    /// set low here so the recovery half of the demo finishes in one run
    /// rather than one coffee break.
    /// </summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Fails fast at startup on a configuration that cannot behave as
    /// described. Polly validates its own ranges when the pipeline is built,
    /// but it cannot know that these two timeouts have a relationship, and
    /// that is the one most likely to be got wrong.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BaseAddress) ||
            !Uri.TryCreate(BaseAddress, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                $"Resilience:BaseAddress must be an absolute URI. Got '{BaseAddress}'.");
        }

        if (AttemptTimeout >= TotalRequestTimeout)
        {
            throw new InvalidOperationException(
                $"Resilience:AttemptTimeout ({AttemptTimeout}) must be strictly less than " +
                $"Resilience:TotalRequestTimeout ({TotalRequestTimeout}). An attempt timeout " +
                "that is not smaller than the total budget can never fire first, which makes " +
                "the retry loop unable to complete a second attempt.");
        }

        if (MaxConcurrentCalls < 1)
        {
            throw new InvalidOperationException(
                $"Resilience:MaxConcurrentCalls must be at least 1. Got {MaxConcurrentCalls}.");
        }

        if (QueueLimit < 0)
        {
            throw new InvalidOperationException(
                $"Resilience:QueueLimit cannot be negative. Got {QueueLimit}.");
        }

        if (MaxRetryAttempts < 1)
        {
            throw new InvalidOperationException(
                $"Resilience:MaxRetryAttempts must be at least 1 (Polly's floor). Got " +
                $"{MaxRetryAttempts}. A pipeline that should not retry at all is one that does " +
                "not add the retry strategy.");
        }

        if (FailureRatio is <= 0 or > 1)
        {
            throw new InvalidOperationException(
                $"Resilience:FailureRatio must be greater than 0 and at most 1. Got {FailureRatio}.");
        }

        if (MinimumThroughput < 2)
        {
            throw new InvalidOperationException(
                $"Resilience:MinimumThroughput must be at least 2 (Polly's floor). Got {MinimumThroughput}.");
        }

        if (SamplingDuration < TimeSpan.FromMilliseconds(500))
        {
            throw new InvalidOperationException(
                $"Resilience:SamplingDuration must be at least 500ms (Polly's floor). Got {SamplingDuration}.");
        }

        if (BreakDuration < TimeSpan.FromMilliseconds(500))
        {
            throw new InvalidOperationException(
                $"Resilience:BreakDuration must be at least 500ms (Polly's floor). Got {BreakDuration}.");
        }
    }
}
