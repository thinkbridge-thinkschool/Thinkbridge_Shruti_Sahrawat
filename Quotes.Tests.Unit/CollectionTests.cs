using FluentAssertions;
using QuotesApi.Domain;

namespace Quotes.Tests.Unit;

public class CollectionTests
{
    // The aggregate takes the instant rather than reading the clock, so every
    // AddItem call in these tests states the time explicitly.
    private static readonly DateTimeOffset At = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_ValidNameAndOwnerId_SetsProperties()
    {
        var collection = new Collection("My Collection", "owner-1");

        collection.Name.Should().Be("My Collection");
        collection.OwnerId.Should().Be("owner-1");
        collection.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ab")]
    public void Constructor_NameShorterThanMinimumOrBlank_ThrowsInvalidOperationException(string? name)
    {
        var act = () => new Collection(name!, "owner-1");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_NameExceeding80Chars_ThrowsInvalidOperationException()
    {
        var tooLongName = new string('a', 81);

        var act = () => new Collection(tooLongName, "owner-1");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_NameExactly3Chars_DoesNotThrow()
    {
        var act = () => new Collection("abc", "owner-1");

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_NameExactly80Chars_DoesNotThrow()
    {
        var maxLengthName = new string('a', 80);

        var act = () => new Collection(maxLengthName, "owner-1");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Constructor_EmptyOrWhitespaceOwnerId_ThrowsInvalidOperationException(string? ownerId)
    {
        var act = () => new Collection("My Collection", ownerId!);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SetName_ValidNameWithSurroundingWhitespace_TrimsName()
    {
        var collection = new Collection("My Collection", "owner-1");

        collection.SetName("  Renamed Collection  ");

        collection.Name.Should().Be("Renamed Collection");
    }

    [Fact]
    public void AddItem_NewQuoteId_AddsToItemsCollection()
    {
        var collection = new Collection("My Collection", "owner-1");

        collection.AddItem(42, At);

        collection.Items.Should().ContainSingle(i => i.QuoteId == 42);
    }

    [Fact]
    public void AddItem_DuplicateQuoteId_ThrowsInvalidOperationException()
    {
        var collection = new Collection("My Collection", "owner-1");
        collection.AddItem(100, At);

        var act = () => collection.AddItem(100, At);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddItem_WhenAt50Items_ThrowsInvalidOperationExceptionOnNextAdd()
    {
        var collection = new Collection("My Collection", "owner-1");
        for (var i = 1; i <= 50; i++)
        {
            collection.AddItem(i, At);
        }

        var act = () => collection.AddItem(51, At);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddItem_When49Items_AllowsAddingThe50th()
    {
        var collection = new Collection("My Collection", "owner-1");
        for (var i = 1; i <= 49; i++)
        {
            collection.AddItem(i, At);
        }

        var act = () => collection.AddItem(50, At);

        act.Should().NotThrow();
    }

    [Fact]
    public void RemoveItem_ExistingQuoteId_RemovesFromItemsCollection()
    {
        var collection = new Collection("My Collection", "owner-1");
        collection.AddItem(42, At);

        collection.RemoveItem(42);

        collection.Items.Should().BeEmpty();
    }

    [Fact]
    public void RemoveItem_NonExistentQuoteId_ThrowsInvalidOperationException()
    {
        var collection = new Collection("My Collection", "owner-1");

        var act = () => collection.RemoveItem(999);

        act.Should().Throw<InvalidOperationException>();
    }
}
