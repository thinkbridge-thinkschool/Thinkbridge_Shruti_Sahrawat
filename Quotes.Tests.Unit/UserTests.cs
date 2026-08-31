using FluentAssertions;
using QuotesApi.Models;

namespace Quotes.Tests.Unit;

public class UserTests
{
    private static readonly DateTimeOffset At = TestClock.DefaultInstant;

    [Theory]
    [InlineData("  Ada@Example.COM  ", "ada@example.com")]
    [InlineData("ada@example.com", "ada@example.com")]
    [InlineData("ADA@EXAMPLE.COM", "ada@example.com")]
    public void Create_NormalisesTheEmail(string input, string expected)
    {
        var user = User.Create(input, "a-hash", Roles.User, At);

        // Without this, one person can end up with two accounts: a
        // case-sensitive unique index sees Ada@ and ada@ as different rows, so
        // the second registration succeeds instead of being rejected, and
        // signing in afterwards depends on capitalising your own address the
        // same way twice.
        user.Email.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_WithoutAnEmail_Throws(string? email)
    {
        var act = () => User.Create(email!, "a-hash", Roles.User, At);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("@example.com")]
    [InlineData("ada@")]
    public void Create_WithAnEmailThatIsNotAnAddress_Throws(string email)
    {
        var act = () => User.Create(email, "a-hash", Roles.User, At);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Create_WithAnEmailAtTheLengthLimit_IsAccepted()
    {
        // 256 exactly - the boundary the column is sized for. A test at 255
        // and a test at 257 would both pass against an off-by-one.
        var local = new string('a', User.MaxEmailLength - "@example.com".Length);
        var email = local + "@example.com";
        email.Length.Should().Be(User.MaxEmailLength);

        var act = () => User.Create(email, "a-hash", Roles.User, At);

        act.Should().NotThrow();
    }

    [Fact]
    public void Create_WithAnEmailOverTheLengthLimit_Throws()
    {
        var email = new string('a', User.MaxEmailLength) + "@example.com";

        var act = () => User.Create(email, "a-hash", Roles.User, At);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutAPasswordHash_Throws(string hash)
    {
        var act = () => User.Create("ada@example.com", hash, Roles.User, At);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Create_WithARoleTheApiDoesNotKnow_Throws()
    {
        // The role is decided by the server from configuration, never sent by a
        // client - but the entity refuses an unknown one anyway, so a typo in
        // some future seeder cannot quietly create an account belonging to a
        // role no authorisation policy will ever match.
        var act = () => User.Create("ada@example.com", "a-hash", "superuser", At);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Create_StampsCreatedAtFromTheSuppliedInstant()
    {
        var instant = new DateTimeOffset(1969, 7, 20, 20, 17, 0, TimeSpan.Zero);

        var user = User.Create("ada@example.com", "a-hash", Roles.User, instant);

        user.CreatedAt.Should().Be(instant.UtcDateTime);
    }

    [Fact]
    public void Create_FromAClock_UsesThatClocksInstant()
    {
        var clock = new TestClock(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var user = User.Create("ada@example.com", "a-hash", Roles.User, clock);

        user.CreatedAt.Should().Be(clock.UtcNow.UtcDateTime);
    }

    [Fact]
    public void NormalizeEmail_OnNull_ReturnsEmptyRatherThanThrowing()
    {
        // Called on request bodies, where a missing field arrives as null
        // before validation has had a chance to reject it.
        User.NormalizeEmail(null!).Should().BeEmpty();
    }
}
