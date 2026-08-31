namespace QuotesApi.Models;

/// <summary>
/// The two roles this API knows about.
/// </summary>
/// <remarks>
/// Constants rather than bare strings at each call site: a typo in
/// <c>[Authorize(Roles = "admin")]</c> does not fail to compile, it fails to
/// authorise - silently, at runtime, and only for the people the policy was
/// supposed to let through.
/// </remarks>
public static class Roles
{
    public const string User = "user";
    public const string Admin = "admin";
}
