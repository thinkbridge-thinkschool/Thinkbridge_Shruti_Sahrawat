using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using OrderRefactor.Authentication;
using Xunit;

namespace OrderRefactor.Tests;

/// <summary>
/// Covers the dual-scheme router: given an Authorization header, which validator
/// gets the request.
/// </summary>
/// <remarks>
/// These are unit tests on purpose. Reaching this logic through the HTTP pipeline
/// means the Entra bearer handler tries to fetch its signing keys from
/// login.microsoftonline.com, which turns a pure string decision into a test that
/// needs the internet and a live tenant. The routing decision is separable, so it
/// is separated and tested here in microseconds.
///
/// What this does NOT prove: that a real Entra-issued token validates. Nothing in
/// this repository proves that — see docs/ENTRA-VERIFICATION.md.
/// </remarks>
public class IssuerSchemeSelectorTests
{
    private const string InternalIssuer = "OrderRefactorIssuer";
    private const string SigningKey = "IssuerSchemeSelectorTests-key-at-least-32-bytes";
    private const string EntraIssuer =
        "https://login.microsoftonline.com/8d46a076-d093-416d-a57b-8692cde13bf8/v2.0";

    [Fact]
    public void SelectScheme_NoAuthorizationHeader_FallsBackToInternal()
    {
        var scheme = IssuerSchemeSelector.SelectScheme(null, InternalIssuer);

        Assert.Equal(IssuerSchemeSelector.InternalScheme, scheme);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Bearer    ")]
    [InlineData("Bearer not-a-jwt")]
    [InlineData("Bearer aaa.bbb")]
    [InlineData("Bearer ...")]
    public void SelectScheme_UnusableHeader_FallsBackToInternal(string header)
    {
        var scheme = IssuerSchemeSelector.SelectScheme(header, InternalIssuer);

        // The fallback must be the local validator, never the remote one. Routing
        // junk to the Entra scheme would let an unauthenticated caller trigger an
        // outbound metadata fetch on every request.
        Assert.Equal(IssuerSchemeSelector.InternalScheme, scheme);
    }

    [Fact]
    public void SelectScheme_TokenFromOurOwnIssuer_RoutesToInternal()
    {
        var header = $"Bearer {TokenWithIssuer(InternalIssuer)}";

        var scheme = IssuerSchemeSelector.SelectScheme(header, InternalIssuer);

        Assert.Equal(IssuerSchemeSelector.InternalScheme, scheme);
    }

    [Fact]
    public void SelectScheme_TokenFromEntra_RoutesToEntra()
    {
        var header = $"Bearer {TokenWithIssuer(EntraIssuer)}";

        var scheme = IssuerSchemeSelector.SelectScheme(header, InternalIssuer);

        Assert.Equal(IssuerSchemeSelector.EntraScheme, scheme);
    }

    [Fact]
    public void SelectScheme_TokenFromAnUnknownIssuer_FallsBackToInternal()
    {
        var header = $"Bearer {TokenWithIssuer("https://evil.example.com/")}";

        var scheme = IssuerSchemeSelector.SelectScheme(header, InternalIssuer);

        Assert.Equal(IssuerSchemeSelector.InternalScheme, scheme);
    }

    /// <summary>
    /// The attack the router must not fall for, and the reason reading an
    /// unvalidated claim here is safe.
    /// </summary>
    [Fact]
    public void SelectScheme_TokenSignedWithOurKeyButClaimingEntraIssuer_RoutesToEntra()
    {
        // Forged issuer, signed with the internal symmetric key an attacker would
        // have to steal anyway.
        var header = $"Bearer {TokenWithIssuer(EntraIssuer)}";

        var scheme = IssuerSchemeSelector.SelectScheme(header, InternalIssuer);

        // It goes to Entra, where validation is STRICTER, not weaker: Entra checks
        // the signature against Microsoft's published keys, which this token was
        // not signed with, so it is rejected. Lying about the issuer buys an
        // attacker a harder validator, not an easier one. If this ever returned
        // InternalScheme, a forged issuer would be validated with a key the
        // attacker already controls.
        Assert.Equal(IssuerSchemeSelector.EntraScheme, scheme);
    }

    /// <summary>
    /// Regression guard for the bug this extraction fixed: the old inline lambda
    /// compared against a hardcoded "OrderRefactorIssuer" while the validator it
    /// routed to read ValidIssuer from configuration.
    /// </summary>
    [Fact]
    public void SelectScheme_WhenInternalIssuerIsReconfigured_StillRoutesOurOwnTokensInternally()
    {
        const string renamedIssuer = "SomeOtherIssuerName";
        var header = $"Bearer {TokenWithIssuer(renamedIssuer)}";

        var scheme = IssuerSchemeSelector.SelectScheme(header, renamedIssuer);

        Assert.Equal(IssuerSchemeSelector.InternalScheme, scheme);
    }

    [Fact]
    public void SelectScheme_WhenInternalIssuerIsReconfigured_OldHardcodedNameNoLongerMatches()
    {
        // The mirror of the test above. With Jwt:Issuer renamed, a token still
        // claiming the old name is not ours, and must not be treated as ours.
        var header = $"Bearer {TokenWithIssuer("OrderRefactorIssuer")}";

        var scheme = IssuerSchemeSelector.SelectScheme(header, "SomeOtherIssuerName");

        Assert.Equal(IssuerSchemeSelector.InternalScheme, scheme);
    }

    [Theory]
    [InlineData("bearer")]
    [InlineData("BEARER")]
    [InlineData("BeArEr")]
    public void SelectScheme_BearerPrefixIsCaseInsensitive(string prefix)
    {
        // RFC 7235 says the auth scheme token is case-insensitive. The original
        // implementation used an ordinal StartsWith("Bearer ") and would have
        // mis-routed a spec-compliant client that sent lowercase.
        var header = $"{prefix} {TokenWithIssuer(EntraIssuer)}";

        var scheme = IssuerSchemeSelector.SelectScheme(header, InternalIssuer);

        Assert.Equal(IssuerSchemeSelector.EntraScheme, scheme);
    }

    [Fact]
    public void EntraIssuerPrefix_MatchesTheV2EndpointShape()
    {
        // Cheap guard on a constant that is easy to typo and expensive to get
        // wrong: a bad prefix silently routes every Entra token to the internal
        // validator, which rejects all of them.
        Assert.StartsWith(IssuerSchemeSelector.EntraIssuerPrefix, EntraIssuer);
    }

    private static string TokenWithIssuer(string issuer)
    {
        var handler = new JwtSecurityTokenHandler();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = "any-audience",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "test@example.com") }),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256Signature)
        };

        return handler.WriteToken(handler.CreateToken(descriptor));
    }
}
