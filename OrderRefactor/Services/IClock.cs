namespace OrderRefactor.Services;

/// <summary>
/// The one place in this application allowed to know what time it is.
/// </summary>
/// <remarks>
/// Registered as a singleton: it holds no state and its answer does not belong
/// to any one request, so a new instance per request would buy nothing.
///
/// Everything time-dependent here is security-relevant — access token expiry,
/// refresh token expiry, revocation timestamps. Reading DateTime.UtcNow inline
/// meant none of it could be tested without either sleeping for the lifetime of
/// a token or hand-writing an expired row straight into the database, bypassing
/// the code path under test. With the clock injected, a test moves time forward
/// and exercises the real branch.
/// </remarks>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
