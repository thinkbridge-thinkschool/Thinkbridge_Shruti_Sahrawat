namespace QuotesApi.Services;

/// <summary>
/// Turns a plaintext password into a hash, and checks one against a hash.
/// </summary>
/// <remarks>
/// An interface rather than static BCrypt calls scattered through the
/// endpoints, for the usual reason plus one specific to hashing: the work
/// factor is a number that has to go up over the years as hardware gets
/// faster, and a single implementation is the only place that stays cheap to
/// change.
/// </remarks>
public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>
    /// True when <paramref name="password"/> produced <paramref name="hash"/>.
    /// Returns false rather than throwing on a malformed hash - a corrupt row
    /// should fail one sign-in, not 500 the endpoint.
    /// </summary>
    bool Verify(string password, string hash);
}
