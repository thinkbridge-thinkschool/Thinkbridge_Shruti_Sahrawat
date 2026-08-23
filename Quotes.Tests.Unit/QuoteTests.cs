using FluentAssertions;
using QuotesApi.Models;

namespace Quotes.Tests.Unit;

public class QuoteTests
{
    // A fixed instant. The entity no longer reads the ambient clock, so every
    // test states the time it wants and can assert it exactly.
    private static readonly DateTimeOffset At = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_NullEmptyOrWhitespaceAuthor_ThrowsInvalidOperationException(string? author)
    {
        var act = () => Quote.Create(author!, "A valid quote.", At);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_NullEmptyOrWhitespaceText_ThrowsInvalidOperationException(string? text)
    {
        var act = () => Quote.Create("A Valid Author", text!, At);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Create_AuthorExceeding200Chars_ThrowsInvalidOperationException()
    {
        var tooLongAuthor = new string('a', 201);

        var act = () => Quote.Create(tooLongAuthor, "A valid quote.", At);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Create_AuthorExactly200Chars_DoesNotThrow()
    {
        var maxLengthAuthor = new string('a', 200);

        var act = () => Quote.Create(maxLengthAuthor, "A valid quote.", At);

        act.Should().NotThrow();
    }

    [Fact]
    public void Create_TextExceeding1000Chars_ThrowsInvalidOperationException()
    {
        var tooLongText = new string('a', 1001);

        var act = () => Quote.Create("A Valid Author", tooLongText, At);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Create_TextExactly1000Chars_DoesNotThrow()
    {
        var maxLengthText = new string('a', 1000);

        var act = () => Quote.Create("A Valid Author", maxLengthText, At);

        act.Should().NotThrow();
    }

    [Fact]
    public void Create_ValidInputWithSurroundingWhitespace_TrimsAuthorAndText()
    {
        var quote = Quote.Create("  Ada Lovelace  ", "  The engine can do whatever we know how to order it to perform.  ", At);

        quote.Author.Should().Be("Ada Lovelace");
        quote.Text.Should().Be("The engine can do whatever we know how to order it to perform.");
    }

    [Fact]
    public void Create_ValidInput_SetsIsDeletedFalse()
    {
        var quote = Quote.Create("Ada Lovelace", "A valid quote.", At);

        quote.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Create_WithGivenInstant_SetsCreatedAtToExactlyThatInstant()
    {
        var quote = Quote.Create("Ada Lovelace", "A valid quote.", At);

        // Exact, not "close to now". This is the whole point of taking the
        // timestamp as a parameter: the assertion has no tolerance window and
        // cannot flake on a slow machine.
        quote.CreatedAt.Should().Be(At.UtcDateTime);
        quote.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void SoftDelete_OnActiveQuote_SetsIsDeletedTrue()
    {
        var quote = Quote.Create("Ada Lovelace", "A valid quote.", At);

        quote.SoftDelete();

        quote.IsDeleted.Should().BeTrue();
    }
}
