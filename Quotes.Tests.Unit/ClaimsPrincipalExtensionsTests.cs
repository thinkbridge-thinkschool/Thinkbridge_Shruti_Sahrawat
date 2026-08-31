using System.Security.Claims;
using FluentAssertions;
using QuotesApi.Extensions;
using QuotesApi.Models;

namespace Quotes.Tests.Unit;

/// <summary>
/// The authorisation helpers every quotes endpoint leans on.
/// </summary>
/// <remarks>
/// Worth testing directly rather than only through the endpoints, because the
/// failure mode of these three methods is not an exception or a wrong status
/// code - it is one user quietly operating on another user's rows, which looks
/// exactly like success from the outside.
/// </remarks>
public class ClaimsPrincipalExtensionsTests
{
    private static ClaimsPrincipal PrincipalWith(params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType: "Test", ClaimTypes.Name, ClaimTypes.Role));

    private static ClaimsPrincipal User(int id, string role = Roles.User)
        => PrincipalWith(
            new Claim(ClaimTypes.NameIdentifier, id.ToString()),
            new Claim(ClaimTypes.Role, role));

    [Fact]
    public void UserId_ReadsTheIdFromTheToken()
    {
        User(42).UserId().Should().Be(42);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-1")]
    public void UserId_OnAnUnusableClaim_ReturnsNull(string raw)
    {
        // Null, never 0. Zero compares equal to a real column default, so a bug
        // that let it through would match rows rather than matching nothing -
        // failing open, which is the one direction this must never fail.
        PrincipalWith(new Claim(ClaimTypes.NameIdentifier, raw)).UserId().Should().BeNull();
    }

    [Fact]
    public void UserId_WithNoIdClaimAtAll_ReturnsNull()
    {
        // A token signed with our key but carrying no id: valid to the
        // authentication layer, useless for attributing rows.
        PrincipalWith(new Claim(ClaimTypes.Role, Roles.User)).UserId().Should().BeNull();
    }

    [Fact]
    public void OwnerFilterFor_AnOrdinaryUser_FiltersToTheirOwnRows()
    {
        User(42).OwnerFilterFor(42).Should().Be(42);
    }

    [Fact]
    public void OwnerFilterFor_AnAdmin_AppliesNoFilter()
    {
        User(1, Roles.Admin).OwnerFilterFor(1).Should().BeNull();
    }

    [Fact]
    public void CanAccessQuoteOwnedBy_TheirOwnQuote_IsAllowed()
    {
        User(42).CanAccessQuoteOwnedBy(ownerId: 42, userId: 42).Should().BeTrue();
    }

    [Fact]
    public void CanAccessQuoteOwnedBy_SomeoneElsesQuote_IsRefused()
    {
        User(42).CanAccessQuoteOwnedBy(ownerId: 7, userId: 42).Should().BeFalse();
    }

    [Fact]
    public void CanAccessQuoteOwnedBy_AnUnownedQuote_IsRefusedForAnOrdinaryUser()
    {
        // Quotes created before accounts existed. Handing them to whichever
        // user asked first would be inventing an owner the data never had.
        User(42).CanAccessQuoteOwnedBy(ownerId: null, userId: 42).Should().BeFalse();
    }

    [Fact]
    public void CanAccessQuoteOwnedBy_AnUnownedQuote_IsAllowedForAnAdmin()
    {
        User(1, Roles.Admin).CanAccessQuoteOwnedBy(ownerId: null, userId: 1).Should().BeTrue();
    }

    [Fact]
    public void CanAccessQuoteOwnedBy_SomeoneElsesQuote_IsAllowedForAnAdmin()
    {
        User(1, Roles.Admin).CanAccessQuoteOwnedBy(ownerId: 7, userId: 1).Should().BeTrue();
    }
}
