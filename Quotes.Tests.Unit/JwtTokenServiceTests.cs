using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Configuration;
using QuotesApi.Extensions;
using QuotesApi.Models;
using QuotesApi.Services;

namespace Quotes.Tests.Unit;

public class JwtTokenServiceTests
{
    // Long enough to clear the 32-byte HMAC-SHA256 minimum. It signs nothing
    // outside this test process.
    private const string Key = "unit-tests-only-signing-key-not-a-secret";
    private const string Issuer = "QuotesApi";
    private const string Audience = "QuotesApi";

    private static JwtTokenService ServiceWith(TestClock clock, TimeSpan? lifetime = null, string key = Key)
        => new(
            Options.Create(new JwtOptions
            {
                Key = key,
                Issuer = Issuer,
                Audience = Audience,
                AccessTokenLifetime = lifetime ?? TimeSpan.FromHours(8)
            }),
            clock);

    /// <summary>
    /// A user with an id, which <see cref="User.Create"/> alone cannot produce.
    /// </summary>
    /// <remarks>
    /// Id is database-assigned and has a private setter, which is the right
    /// shape for the entity - nothing in the application should be inventing
    /// primary keys. Reflection here is the narrower compromise: the
    /// alternative is a public setter that only tests want, weakening the
    /// entity permanently so one test file can be prettier.
    /// </remarks>
    private static User UserWithId(int id, string email = "ada@example.com", string role = Roles.User)
    {
        var user = User.Create(email, "a-hash", role, TestClock.DefaultInstant);
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, id);
        return user;
    }

    /// <summary>
    /// Validates the token the way Program.cs does and hands back the principal.
    /// </summary>
    /// <remarks>
    /// Reading the raw JWT's claims instead would assert against the short wire
    /// names ("nameid", "role") rather than what the API actually reads, and
    /// would pass even if issuer, audience or signature were wrong. Lifetime
    /// validation is the one thing switched off: the token is minted against a
    /// frozen clock in 2026, and the validator would compare it against the
    /// real one.
    /// </remarks>
    private static ClaimsPrincipal Validate(string token, string key = Key)
        => new JwtSecurityTokenHandler().ValidateToken(
            token,
            new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = Issuer,
                ValidateAudience = true,
                ValidAudience = Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                ValidateLifetime = false,
                NameClaimType = ClaimTypes.Name,
                RoleClaimType = ClaimTypes.Role
            },
            out _);

    [Fact]
    public void CreateAccessToken_CarriesTheUsersIdEmailAndRole()
    {
        var service = ServiceWith(new TestClock());

        var token = service.CreateAccessToken(UserWithId(42, "ada@example.com", Roles.User));

        var principal = Validate(token);
        principal.UserId().Should().Be(42);
        principal.FindFirstValue(ClaimTypes.Name).Should().Be("ada@example.com");
        principal.IsAdmin().Should().BeFalse();
    }

    [Fact]
    public void CreateAccessToken_ForAnAdmin_ProducesAPrincipalInTheAdminRole()
    {
        var service = ServiceWith(new TestClock());

        var token = service.CreateAccessToken(UserWithId(1, "boss@example.com", Roles.Admin));

        // This is the assertion that would fail if JwtTokenService and
        // Program.cs ever disagreed about which claim type carries the role -
        // a mismatch that does not error, it just quietly puts nobody in any
        // role and gives the admin an ordinary user's view.
        Validate(token).IsAdmin().Should().BeTrue();
    }

    [Fact]
    public void CreateAccessToken_ExpiresOneLifetimeAfterTheClock_NotAfterTheWallClock()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.Zero));
        var service = ServiceWith(clock, TimeSpan.FromHours(8));

        var token = service.CreateAccessToken(UserWithId(42));

        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);
        parsed.ValidTo.Should().Be(clock.UtcNow.UtcDateTime.AddHours(8));

        // Pinned to the same instant rather than read three separate times, so
        // a token minted against a moving clock cannot end up with a "not
        // before" later than its own issue time.
        parsed.ValidFrom.Should().Be(clock.UtcNow.UtcDateTime);
    }

    [Fact]
    public void CreateAccessToken_MovingTheClockForward_MovesTheExpiryWithIt()
    {
        var clock = new TestClock();
        var service = ServiceWith(clock, TimeSpan.FromHours(1));

        var first = new JwtSecurityTokenHandler().ReadJwtToken(service.CreateAccessToken(UserWithId(42)));
        clock.Advance(TimeSpan.FromDays(2));
        var second = new JwtSecurityTokenHandler().ReadJwtToken(service.CreateAccessToken(UserWithId(42)));

        second.ValidTo.Should().Be(first.ValidTo.AddDays(2));
    }

    [Fact]
    public void CreateAccessToken_TwoTokensForTheSameUser_AreNotIdentical()
    {
        var service = ServiceWith(new TestClock());
        var user = UserWithId(42);

        // Same user, same frozen instant, so every claim but jti is identical.
        // The jti is what a future revocation list would key on, and it is why
        // two tokens minted in the same second are still distinguishable.
        service.CreateAccessToken(user).Should().NotBe(service.CreateAccessToken(user));
    }

    [Fact]
    public void CreateAccessToken_IsRejectedWhenValidatedWithADifferentKey()
    {
        var service = ServiceWith(new TestClock());

        var token = service.CreateAccessToken(UserWithId(42));

        var act = () => Validate(token, "a-completely-different-signing-key-32b+");

        act.Should().Throw<SecurityTokenException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("31-bytes-is-one-short-of-enough")]
    public void Constructor_WithAKeyShorterThanHmacSha256Needs_ThrowsWithAnActionableMessage(string key)
    {
        // The crypto layer throws for a short key too, but with a message about
        // key sizes that does not obviously mean "the value in your
        // configuration is too short" - and it throws on first use rather than
        // at startup, so the failure lands on a user's sign-in attempt.
        var act = () => ServiceWith(new TestClock(), key: key);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Jwt:Key*");
    }
}
