using FluentAssertions;
using QuotesApi.Domain;

namespace Quotes.Tests.Unit;

public class CollectionItemTests
{
    private static readonly DateTimeOffset At = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_PositiveQuoteId_SetsQuoteId()
    {
        var item = new CollectionItem(42, At);

        item.QuoteId.Should().Be(42);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Constructor_ZeroOrNegativeQuoteId_ThrowsArgumentException(int quoteId)
    {
        var act = () => new CollectionItem(quoteId, At);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WithGivenInstant_SetsAddedAtToExactlyThatInstant()
    {
        var item = new CollectionItem(42, At);

        item.AddedAt.Should().Be(At.UtcDateTime);
        item.AddedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Constructor_WithNonUtcOffset_NormalisesAddedAtToUtc()
    {
        // Nine-and-a-half hours ahead: if the offset were dropped rather than
        // converted, AddedAt would land in the future by that much.
        var offsetInstant = new DateTimeOffset(2026, 3, 14, 19, 0, 0, TimeSpan.FromHours(9.5));

        var item = new CollectionItem(42, offsetInstant);

        item.AddedAt.Should().Be(new DateTime(2026, 3, 14, 9, 30, 0, DateTimeKind.Utc));
    }
}
