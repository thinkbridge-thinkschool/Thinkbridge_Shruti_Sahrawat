using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;

namespace OrderRefactor.Authentication;

/// <summary>
/// Decides which JWT bearer scheme validates an incoming request: the internal
/// symmetric-key one, or the Entra ID one that checks signatures against
/// Microsoft's published keys.
/// </summary>
/// <remarks>
/// This used to be a lambda inside <c>Program.cs</c>. It was pulled out for two
/// reasons.
///
/// First, it is the only branching logic in the authentication pipeline, and
/// inside a lambda in <c>Program.cs</c> the only way to reach it was to boot the
/// whole app and let the Entra handler make a live network call to
/// <c>login.microsoftonline.com</c> for its key set. That makes the test slow,
/// internet-dependent, and flaky for a decision that is pure string handling.
///
/// Second, the lambda compared against a hardcoded "OrderRefactorIssuer" while
/// the validator it routes to reads <c>ValidIssuer</c> from configuration. The
/// two would silently disagree the moment <c>Jwt:Issuer</c> changed, and every
/// internally-issued token would be routed to the Entra validator and rejected.
/// The issuer is now a parameter, supplied from the same options object the
/// validator uses.
///
/// On reading an unvalidated claim: this only chooses which validator runs.
/// Both validators then perform full signature, issuer, audience and lifetime
/// validation. Claiming to be Entra does not skip validation — it opts the
/// caller into signature checking against keys only Microsoft holds.
/// </remarks>
public static class IssuerSchemeSelector
{
    public const string InternalScheme = "InternalJwt";
    public const string EntraScheme = "EntraJwt";
    public const string PolicyScheme = "PolicyScheme";

    /// <summary>Every Entra v2.0 issuer is this prefix plus the tenant id.</summary>
    public const string EntraIssuerPrefix = "https://login.microsoftonline.com/";

    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Returns the name of the scheme that should handle this request.
    /// </summary>
    /// <param name="authorizationHeader">Raw Authorization header, or null when absent.</param>
    /// <param name="internalIssuer">The <c>Jwt:Issuer</c> this API signs its own tokens with.</param>
    /// <returns>
    /// <see cref="EntraScheme"/> only for a readable token whose issuer looks like
    /// Entra. Everything else — no header, a non-Bearer header, an unreadable
    /// token, an unrecognised issuer — falls back to <see cref="InternalScheme"/>,
    /// which then rejects it. The fallback must be the stricter local validator,
    /// never the remote one, so that a junk token cannot cause an outbound
    /// metadata fetch.
    /// </returns>
    public static string SelectScheme(string? authorizationHeader, string internalIssuer)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
            return InternalScheme;

        if (!authorizationHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return InternalScheme;

        var token = authorizationHeader[BearerPrefix.Length..].Trim();
        if (token.Length == 0)
            return InternalScheme;

        var handler = new JwtSecurityTokenHandler();

        // CanReadToken answers the common malformed case without throwing, which
        // matters because this runs on every authenticated request.
        if (!handler.CanReadToken(token))
            return InternalScheme;

        string? issuer;
        try
        {
            issuer = handler.ReadJwtToken(token).Issuer;
        }
        catch (SecurityTokenException)
        {
            // Readable shape, unreadable content. Narrow catch, not catch-all:
            // an unexpected exception type here is a bug worth surfacing rather
            // than swallowing into a silent 401.
            return InternalScheme;
        }
        catch (ArgumentException)
        {
            return InternalScheme;
        }

        if (string.Equals(issuer, internalIssuer, StringComparison.Ordinal))
            return InternalScheme;

        if (issuer is not null
            && issuer.StartsWith(EntraIssuerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return EntraScheme;
        }

        return InternalScheme;
    }
}
