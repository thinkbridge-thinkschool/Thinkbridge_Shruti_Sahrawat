using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using QuotesApi.Extensions;
using QuotesApi.Models;

namespace Quotes.Tests.Integration;

/// <summary>
/// The Day 11 profiling pair: one endpoint written with the anti-patterns, one
/// written correctly, both returning the same data.
/// </summary>
/// <remarks>
/// "Both returning the same data" is the part that was never checked. A 241x p99
/// improvement is only an improvement if the fast version still answers the
/// question — otherwise it is just a faster wrong answer, and load-testing
/// numbers say nothing about that. The two endpoints also drifted apart in a way
/// that is easy to miss by eye: the slow one aggregates in memory after
/// materialising every row, the fast one aggregates in SQL with a GroupBy that
/// EF has to translate. Those are different enough to disagree.
/// </remarks>
[Collection(MsSqlCollection.Name)]
public class ProfilingEndpointsTests
{
    private readonly MsSqlContainerFixture _sqlServer;

    public ProfilingEndpointsTests(MsSqlContainerFixture sqlServer) => _sqlServer = sqlServer;

    private static async Task SeedAsync(HttpClient client)
    {
        var quotes = new (string Author, string Text)[]
        {
            ("Ada Lovelace", "The Analytical Engine weaves algebraic patterns."),
            ("Ada Lovelace", "That brain of mine is something more than merely mortal."),
            ("Ada Lovelace", "Imagination is the discovering faculty."),
            ("Grace Hopper", "It is easier to ask forgiveness than permission."),
            ("Grace Hopper", "A ship in port is safe, but that is not what ships are for."),
            ("Alan Turing", "We can only see a short distance ahead.")
        };

        foreach (var (author, text) in quotes)
        {
            var response = await client.PostAsJsonAsync(
                "/api/quotes", new CreateQuoteRequest { Author = author, Text = text });
            response.StatusCode.Should().Be(HttpStatusCode.Created);
        }
    }

    private static async Task<List<AuthorStats>> GetStatsAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var stats = await response.Content.ReadFromJsonAsync<List<AuthorStats>>(TestInfrastructure.Json);
        stats.Should().NotBeNull();
        return stats!;
    }

    [Fact]
    public async Task FastEndpoint_ReturnsOneRowPerAuthorWithTheCorrectCount()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);
        await SeedAsync(host.Client);

        var stats = await GetStatsAsync(host.Client, "/api/profiling/author-stats-fast");

        stats.Should().HaveCount(3);
        stats.Single(s => s.Author == "Ada Lovelace").QuoteCount.Should().Be(3);
        stats.Single(s => s.Author == "Grace Hopper").QuoteCount.Should().Be(2);
        stats.Single(s => s.Author == "Alan Turing").QuoteCount.Should().Be(1);
    }

    [Fact]
    public async Task FastEndpoint_OrdersByQuoteCountDescending()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);
        await SeedAsync(host.Client);

        var stats = await GetStatsAsync(host.Client, "/api/profiling/author-stats-fast");

        stats.Select(s => s.Author).Should().Equal("Ada Lovelace", "Grace Hopper", "Alan Turing");
    }

    [Fact]
    public async Task SlowEndpoint_ReturnsTheSameAnswerAsTheFastOne()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);
        await SeedAsync(host.Client);

        var slow = await GetStatsAsync(host.Client, "/api/profiling/author-stats-slow");
        var fast = await GetStatsAsync(host.Client, "/api/profiling/author-stats-fast");

        // The whole justification for the Day 11 rewrite. Without this, "241x
        // faster" is a claim about a query that might not be answering the same
        // question any more.
        fast.Should().BeEquivalentTo(slow, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task BothEndpoints_WithNoQuotesAtAll_ReturnAnEmptyListRatherThanFailing()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);

        var slow = await GetStatsAsync(host.Client, "/api/profiling/author-stats-slow");
        var fast = await GetStatsAsync(host.Client, "/api/profiling/author-stats-fast");

        // The empty case is where a Max() over an empty sequence would throw.
        // The slow endpoint calls quotes.Max(...) inside its per-author loop; it
        // only survives because the loop body never runs when there are no
        // authors. Worth pinning, because it is one refactor away from breaking.
        slow.Should().BeEmpty();
        fast.Should().BeEmpty();
    }

    [Fact]
    public async Task BothEndpoints_ExcludeSoftDeletedQuotesFromTheCounts()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);
        await SeedAsync(host.Client);

        // DELETE removes the row outright in this API; the IsDeleted filter in
        // both queries is what would keep a soft-deleted quote out. Either way,
        // the two endpoints must agree after the change.
        var listed = await host.Client.GetFromJsonAsync<PagedResult<QuoteResponse>>(
            "/api/quotes?page=1&size=50", TestInfrastructure.Json);
        var target = listed!.Items.First(q => q.Author == "Alan Turing");

        var deleted = await host.Client.DeleteAsync($"/api/quotes/{target.Id}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var slow = await GetStatsAsync(host.Client, "/api/profiling/author-stats-slow");
        var fast = await GetStatsAsync(host.Client, "/api/profiling/author-stats-fast");

        slow.Should().NotContain(s => s.Author == "Alan Turing");
        fast.Should().BeEquivalentTo(slow, options => options.WithStrictOrdering());
    }
}
