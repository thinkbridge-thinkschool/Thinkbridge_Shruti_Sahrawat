namespace QuotesApi.Configuration;

/// <summary>
/// Who gets the admin role, and the password rules applied at registration.
/// </summary>
/// <remarks>
/// AdminEmails is empty in appsettings.json on purpose. The real value is set
/// per-environment (user-secrets locally, a container app environment variable
/// in Azure) so that a personal email address is not committed to a repository
/// that anyone can read - which would both expose it and advertise exactly
/// which account is worth attacking.
///
/// The alternative designs were worse. Hardcoding the address in source has
/// both problems and needs a redeploy to change. "First account to register
/// becomes admin" needs no configuration at all, but on a public URL it is a
/// race the owner can lose to a stranger, permanently.
/// </remarks>
public class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Emails that receive the admin role when they register. Compared
    /// case-insensitively against the normalised (trimmed, lowercased) address,
    /// so "Shruti@Example.com" in configuration matches a registration for
    /// "shruti@example.com".
    /// </summary>
    public string[] AdminEmails { get; set; } = Array.Empty<string>();
}
