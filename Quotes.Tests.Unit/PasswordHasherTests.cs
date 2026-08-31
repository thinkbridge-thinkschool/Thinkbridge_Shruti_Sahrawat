using FluentAssertions;
using QuotesApi.Services;

namespace Quotes.Tests.Unit;

public class PasswordHasherTests
{
    private readonly IPasswordHasher _hasher = new BcryptPasswordHasher();

    [Fact]
    public void Verify_WithTheOriginalPassword_ReturnsTrue()
    {
        var hash = _hasher.Hash("correct-horse-battery");

        _hasher.Verify("correct-horse-battery", hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_WithADifferentPassword_ReturnsFalse()
    {
        var hash = _hasher.Hash("correct-horse-battery");

        _hasher.Verify("correct-horse-batteru", hash).Should().BeFalse();
    }

    [Fact]
    public void Verify_IsCaseSensitive()
    {
        var hash = _hasher.Hash("correct-horse-battery");

        _hasher.Verify("Correct-Horse-Battery", hash).Should().BeFalse();
    }

    [Fact]
    public void Hash_ProducesADifferentHashEachTime_ForTheSamePassword()
    {
        var first = _hasher.Hash("correct-horse-battery");
        var second = _hasher.Hash("correct-horse-battery");

        // BCrypt salts every hash. Without a salt, two people who chose the
        // same password produce identical rows - which tells anyone reading a
        // stolen database exactly which accounts to attack together, and makes
        // a precomputed table worth building.
        first.Should().NotBe(second);
        _hasher.Verify("correct-horse-battery", first).Should().BeTrue();
        _hasher.Verify("correct-horse-battery", second).Should().BeTrue();
    }

    [Fact]
    public void Hash_UsesTheConfiguredWorkFactor()
    {
        var hash = _hasher.Hash("correct-horse-battery");

        // The cost is stored inside the hash itself, which is what allows it to
        // be raised later without invalidating existing hashes. Asserted here
        // so that lowering it - the tempting fix the first time somebody
        // notices login is the slowest endpoint - cannot happen silently.
        hash.Should().Contain($"${BcryptPasswordHasher.WorkFactor}$");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-bcrypt-hash")]
    [InlineData("$2a$truncated")]
    public void Verify_AgainstAMalformedHash_ReturnsFalseRatherThanThrowing(string hash)
    {
        // A corrupt or empty hash column should fail one sign-in with the same
        // 401 as any wrong password - not 500 the endpoint, which would both
        // page somebody and tell the caller that this particular account is
        // different from the others.
        var act = () => _hasher.Verify("correct-horse-battery", hash);

        act.Should().NotThrow();
        _hasher.Verify("correct-horse-battery", hash).Should().BeFalse();
    }
}
