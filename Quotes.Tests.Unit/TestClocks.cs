namespace Quotes.Tests.Unit;

// Two applications, two IClock interfaces, one test project that references
// both. The interfaces are named with their full namespace here so that neither
// `using` has to win — importing both would make the bare name ambiguous.

/// <summary>
/// A clock frozen at a known instant, for QuotesApi.
/// </summary>
/// <remarks>
/// Deliberately a hand-written class rather than an NSubstitute mock. A clock
/// that can be moved forward reads better in a test than a stack of
/// <c>Returns(...)</c> setups, and <see cref="Advance"/> makes "and then two
/// hours later" a single line.
/// </remarks>
public sealed class TestClock : QuotesApi.Services.IClock
{
    /// <summary>Pi day 2026, 09:30 UTC. Arbitrary, fixed, and obviously not "now".</summary>
    public static readonly DateTimeOffset DefaultInstant =
        new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; } = DefaultInstant;

    public TestClock() { }

    public TestClock(DateTimeOffset instant) => UtcNow = instant;

    /// <summary>Moves the clock forward and returns itself, so calls can chain.</summary>
    public TestClock Advance(TimeSpan by)
    {
        UtcNow = UtcNow.Add(by);
        return this;
    }
}

/// <summary>
/// The same thing for OrderRefactor, whose IClock lives in its own namespace.
/// </summary>
public sealed class OrderTestClock : OrderRefactor.Services.IClock
{
    public static readonly DateTimeOffset DefaultInstant =
        new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; } = DefaultInstant;

    public OrderTestClock() { }

    public OrderTestClock(DateTimeOffset instant) => UtcNow = instant;

    public OrderTestClock Advance(TimeSpan by)
    {
        UtcNow = UtcNow.Add(by);
        return this;
    }
}
