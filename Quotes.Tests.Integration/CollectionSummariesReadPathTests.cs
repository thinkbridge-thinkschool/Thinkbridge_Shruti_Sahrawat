using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Domain;
using QuotesApi.Features.Collections;
using QuotesApi.Models;

namespace Quotes.Tests.Integration;

/// <summary>
/// The read side of the CQRS split, both implementations of it.
/// </summary>
/// <remarks>
/// SQLite rather than the SQL Server container these other integration tests
/// use, and that is a deliberate limitation rather than a shortcut. The Dapper
/// handler is hand-written SQL targeting SQLite (see
/// QuotesApi/Features/Collections/DAPPER.md) — running it against SQL Server
/// would be testing a dialect it was never written for. The EF handler is
/// covered against SQL Server too, through
/// <c>GET /api/collections/summaries</c> in CollectionsEndpointsTests.
///
/// The assertion that earns its keep here is the last one: the two
/// implementations must return the same thing. That is the entire claim Day 12
/// task 2 makes, and without this test the claim rests on eyeballing two JSON
/// responses once.
/// </remarks>
public sealed class CollectionSummariesReadPathTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    private readonly DbConnection _connection;
    private readonly DbContextOptions<QuotesDbContext> _options;

    private int _quoteA, _quoteB, _quoteC, _quoteD;

    public CollectionSummariesReadPathTests()
    {
        // In-memory SQLite kept alive by holding the connection open: close it
        // and the database evaporates. One database per test class instance, so
        // xUnit's per-class isolation is the test isolation.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<QuotesDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = NewContext();
        db.Database.EnsureCreated();
        Seed(db);
    }

    private QuotesDbContext NewContext() => new(_options);

    private void Seed(QuotesDbContext db)
    {
        var a = Quote.Create("Ada Lovelace", "The Analytical Engine weaves algebraic patterns.", Base);
        var b = Quote.Create("Grace Hopper", "It is easier to ask forgiveness than permission.", Base);
        var c = Quote.Create("Alan Turing", "We can only see a short distance ahead.", Base);
        var d = Quote.Create("Edsger Dijkstra", "Simplicity is a great virtue.", Base);
        db.Quotes.AddRange(a, b, c, d);
        db.SaveChanges();

        _quoteA = a.Id;
        _quoteB = b.Id;
        _quoteC = c.Id;
        _quoteD = d.Id;

        // Distinct AddedAt values on purpose. Both implementations order the
        // preview by AddedAt descending, so ties would make the comparison
        // between them depend on undefined ordering and the test would flake.
        var pioneers = new Collection("Computing Pioneers", "owner-1");
        pioneers.AddItem(_quoteA, Base.AddMinutes(1));
        pioneers.AddItem(_quoteB, Base.AddMinutes(2));
        pioneers.AddItem(_quoteC, Base.AddMinutes(3));

        var other = new Collection("Someone Else's List", "owner-2");
        other.AddItem(_quoteD, Base.AddMinutes(4));

        var empty = new Collection("Nothing In Here Yet", "owner-1");

        db.Collections.AddRange(pioneers, other, empty);
        db.SaveChanges();
    }

    private async Task<IReadOnlyList<CollectionSummary>> ViaEntityFramework(
        string? ownerId = null, int previewSize = 3)
    {
        await using var db = NewContext();
        return await new GetCollectionSummariesHandler(db).Handle(
            new GetCollectionSummariesQuery(ownerId, previewSize), CancellationToken.None);
    }

    private async Task<IReadOnlyList<CollectionSummary>> ViaDapper(
        string? ownerId = null, int previewSize = 3)
    {
        await using var db = NewContext();
        return await new GetCollectionSummariesDapperHandler(db).Handle(
            new GetCollectionSummariesDapperQuery(ownerId, previewSize), CancellationToken.None);
    }

    [Fact]
    public async Task EntityFramework_NoOwnerFilter_ReturnsEveryCollection()
    {
        var summaries = await ViaEntityFramework();

        summaries.Should().HaveCount(3);
        summaries.Select(s => s.Name).Should().Contain(
            new[] { "Computing Pioneers", "Someone Else's List", "Nothing In Here Yet" });
    }

    [Fact]
    public async Task EntityFramework_WithOwnerFilter_ReturnsOnlyThatOwnersCollections()
    {
        var summaries = await ViaEntityFramework(ownerId: "owner-2");

        summaries.Should().ContainSingle()
            .Which.Name.Should().Be("Someone Else's List");
    }

    [Fact]
    public async Task EntityFramework_CountsEveryItemEvenWhenThePreviewIsSmaller()
    {
        var summaries = await ViaEntityFramework(previewSize: 1);

        var pioneers = summaries.Single(s => s.Name == "Computing Pioneers");

        // ItemCount is the whole collection; Preview is a window onto it. A
        // screen that showed "1 quote" for a collection of three would be a
        // reasonable-looking bug, so the two are asserted separately.
        pioneers.ItemCount.Should().Be(3);
        pioneers.Preview.Should().HaveCount(1);
    }

    [Fact]
    public async Task EntityFramework_OrdersThePreviewNewestFirst()
    {
        var summaries = await ViaEntityFramework(previewSize: 2);

        var pioneers = summaries.Single(s => s.Name == "Computing Pioneers");

        pioneers.Preview.Select(p => p.QuoteId).Should().Equal(_quoteC, _quoteB);
    }

    [Fact]
    public async Task EntityFramework_JoinsTheQuoteTextAndAuthorOntoThePreview()
    {
        var summaries = await ViaEntityFramework();

        var newest = summaries.Single(s => s.Name == "Computing Pioneers").Preview.First();

        // The reason the read model exists at all: the aggregate stores only a
        // QuoteId, and the screen needs words.
        newest.QuoteId.Should().Be(_quoteC);
        newest.Author.Should().Be("Alan Turing");
        newest.Text.Should().Be("We can only see a short distance ahead.");
    }

    [Fact]
    public async Task EntityFramework_EmptyCollection_ReportsZeroItemsAndNoMostRecentDate()
    {
        var summaries = await ViaEntityFramework();

        var empty = summaries.Single(s => s.Name == "Nothing In Here Yet");

        empty.ItemCount.Should().Be(0);
        empty.Preview.Should().BeEmpty();
        // Null, not DateTime.MinValue. A default date here would render as
        // "01/01/0001" on the screen this model exists to feed.
        empty.MostRecentlyAdded.Should().BeNull();
    }

    [Fact]
    public async Task EntityFramework_MostRecentlyAdded_IsTheNewestItemNotTheOldest()
    {
        var summaries = await ViaEntityFramework();

        var pioneers = summaries.Single(s => s.Name == "Computing Pioneers");

        pioneers.MostRecentlyAdded.Should().Be(Base.AddMinutes(3).UtcDateTime);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    public async Task Dapper_MatchesEntityFrameworkAtEveryPreviewSize(int previewSize)
    {
        var viaEf = (await ViaEntityFramework(previewSize: previewSize))
            .OrderBy(s => s.Id).ToList();
        var viaDapper = (await ViaDapper(previewSize: previewSize))
            .OrderBy(s => s.Id).ToList();

        // Strict ordering because preview order is part of the contract: both
        // implementations promise newest first, and a comparison that ignored
        // order would pass while one of them returned the oldest quotes.
        viaDapper.Should().BeEquivalentTo(viaEf, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task Dapper_MatchesEntityFrameworkWhenFilteringByOwner()
    {
        var viaEf = (await ViaEntityFramework(ownerId: "owner-1")).OrderBy(s => s.Id).ToList();
        var viaDapper = (await ViaDapper(ownerId: "owner-1")).OrderBy(s => s.Id).ToList();

        viaDapper.Should().BeEquivalentTo(viaEf, options => options.WithStrictOrdering());
        viaDapper.Should().HaveCount(2);
    }

    [Fact]
    public async Task Dapper_EmptyCollection_ReportsZeroAndNullJustLikeEntityFramework()
    {
        // The LEFT JOIN plus COALESCE path in the Dapper SQL. Get this wrong and
        // a collection with no items disappears from the list entirely, which an
        // equivalence test against a seeded-only-with-items database would miss.
        var summaries = await ViaDapper();

        var empty = summaries.Single(s => s.Name == "Nothing In Here Yet");

        empty.ItemCount.Should().Be(0);
        empty.MostRecentlyAdded.Should().BeNull();
        empty.Preview.Should().BeEmpty();
    }

    [Fact]
    public async Task BothImplementations_DropPreviewItemsWhoseQuoteNoLongerExists()
    {
        // A collection item points at a quote id by value; nothing stops the
        // quote being deleted underneath it. Both handlers guard against the
        // dangling reference rather than throwing or emitting an empty row.
        await using (var db = NewContext())
        {
            var quote = await db.Quotes.FindAsync(_quoteC);
            db.Quotes.Remove(quote!);
            await db.SaveChangesAsync();
        }

        var viaEf = await ViaEntityFramework();
        var viaDapper = await ViaDapper();

        var efPioneers = viaEf.Single(s => s.Name == "Computing Pioneers");
        var dapperPioneers = viaDapper.Single(s => s.Name == "Computing Pioneers");

        efPioneers.Preview.Should().NotContain(p => p.QuoteId == _quoteC);
        dapperPioneers.Preview.Should().NotContain(p => p.QuoteId == _quoteC);

        // ItemCount still counts the orphan: the aggregate genuinely holds three
        // items. Only the preview, which needs the quote text, drops it.
        efPioneers.ItemCount.Should().Be(3);
        dapperPioneers.ItemCount.Should().Be(3);
    }

    public void Dispose() => _connection.Dispose();
}
