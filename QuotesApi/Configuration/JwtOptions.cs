namespace QuotesApi.Configuration;

/// <summary>
/// How access tokens are signed and how long they last.
/// </summary>
/// <remarks>
/// Bound from the "Jwt" configuration section. <see cref="Key"/> deliberately
/// has no default: a signing key with a fallback value is a signing key
/// somebody eventually ships to production without noticing, and anyone who
/// can read this repository could then mint a token for any account. Program.cs
/// refuses to start outside Development if it is missing, rather than starting
/// with a guessable one.
/// </remarks>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// The HMAC-SHA256 signing key. At least 32 bytes - shorter keys are
    /// rejected by the algorithm itself, with an error that does not obviously
    /// say "your key is too short".
    /// </summary>
    public string Key { get; set; } = string.Empty;

    public string Issuer { get; set; } = "QuotesApi";

    public string Audience { get; set; } = "QuotesApi";

    /// <summary>
    /// Eight hours: long enough that a person working through an afternoon is
    /// not signed out mid-sentence, short enough that a token copied out of a
    /// browser is not useful next week. There is no refresh-token rotation in
    /// this API - when the token expires you sign in again. OrderRefactor's
    /// AuthController has a full rotation-with-reuse-detection implementation
    /// if this ever needs one.
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(8);
}
