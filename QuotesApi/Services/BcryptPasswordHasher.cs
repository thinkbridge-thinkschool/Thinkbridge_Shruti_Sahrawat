namespace QuotesApi.Services;

/// <summary>
/// BCrypt, with a deliberately slow work factor.
/// </summary>
/// <remarks>
/// Not SHA-256, and not SHA-256 with a salt either. A general-purpose hash is
/// designed to be fast, which is exactly the wrong property here: fast means a
/// stolen database can be attacked at billions of guesses per second. BCrypt is
/// designed to be slow and to stay slow - the work factor is stored inside each
/// hash, so raising it later does not invalidate existing hashes, and it salts
/// every hash automatically so two people with the same password do not produce
/// the same row.
/// </remarks>
public class BcryptPasswordHasher : IPasswordHasher
{
    /// <summary>
    /// Cost 12: roughly a quarter-second per hash on current hardware.
    /// </summary>
    /// <remarks>
    /// Each step up doubles the work. 12 is slow enough to make offline
    /// guessing expensive and fast enough that a sign-in still feels instant.
    /// It is also the reason login is the slowest endpoint in this API by an
    /// order of magnitude, which is intentional rather than a performance bug
    /// waiting to be "fixed".
    /// </remarks>
    public const int WorkFactor = 12;

    public string Hash(string password)
        => BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

    public bool Verify(string password, string hash)
    {
        // An empty or null hash never reaches BCrypt at all. It throws a plain
        // ArgumentException for this specific case rather than the
        // SaltParseException every other malformed shape produces - caught
        // below - so without this check an empty PasswordHash column would
        // 500 the login endpoint instead of answering the same 401 as a wrong
        // password.
        if (string.IsNullOrEmpty(hash)) return false;

        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (Exception ex) when (ex is BCrypt.Net.SaltParseException or ArgumentException)
        {
            // Every other malformed shape: truncated, or written by some other
            // scheme. Treated as "these credentials do not match" rather than
            // as a server error: the caller gets the same 401 as any other
            // wrong password, and no information about why.
            return false;
        }
    }
}
