using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Configuration;
using QuotesApi.Models;

namespace QuotesApi.Services;

/// <summary>
/// HMAC-SHA256 signed JWTs, with every timestamp taken from <see cref="IClock"/>.
/// </summary>
/// <remarks>
/// The clock is injected for the same reason OrderRefactor's AuthController
/// injects one: expiry is the whole point of a token, and a service that reads
/// DateTime.UtcNow itself can only have its expiry branch tested by sleeping
/// for eight hours or by hand-writing a token and skipping the code under test.
/// </remarks>
public class JwtTokenService : ITokenService
{
    /// <summary>
    /// HMAC-SHA256 needs a key at least as long as its output - 256 bits, 32
    /// bytes. A shorter one throws from inside the crypto layer with a message
    /// about key sizes that does not obviously mean "the value you put in
    /// configuration is too short", so it is checked here instead.
    /// </summary>
    public const int MinimumKeyBytes = 32;

    private readonly JwtOptions _options;
    private readonly IClock _clock;

    public JwtTokenService(IOptions<JwtOptions> options, IClock clock)
    {
        _options = options.Value;
        _clock = clock;

        if (Encoding.UTF8.GetByteCount(_options.Key) < MinimumKeyBytes)
        {
            throw new InvalidOperationException(
                $"Jwt:Key must be at least {MinimumKeyBytes} bytes for HMAC-SHA256. " +
                "Set it with `dotnet user-secrets set \"Jwt:Key\" \"<a long random string>\"` locally, " +
                "or as a container app environment variable in Azure.");
        }
    }

    public TimeSpan AccessTokenLifetime => _options.AccessTokenLifetime;

    public string CreateAccessToken(User user)
    {
        var now = _clock.UtcNow.UtcDateTime;
        var handler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(_options.Key);

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                // The id, not the email, is what ties a quote to its owner.
                // Emails change; a primary key does not, and re-pointing every
                // row a person owns because they changed address is not a
                // migration anyone should have to write.
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Email),
                new Claim(ClaimTypes.Role, user.Role),

                // A unique id per token. Nothing in this API reads it today -
                // it is what a future revocation list would key on, and it also
                // guarantees two tokens minted for the same user in the same
                // second are not byte-identical.
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            }),

            // Pinned to one instant rather than read three times, so a test
            // that moves the clock gets a coherent token instead of one whose
            // "not before" drifts past its own issue time.
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(_options.AccessTokenLifetime),

            Issuer = _options.Issuer,
            Audience = _options.Audience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };

        return handler.WriteToken(handler.CreateToken(descriptor));
    }
}
