using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using QuotesApi.Models;

namespace Quotes.Tests.Integration;

[Collection(MsSqlCollection.Name)]
public class QuoteEndpointsTests
{
    private readonly MsSqlContainerFixture _sqlServer;

    public QuoteEndpointsTests(MsSqlContainerFixture sqlServer) => _sqlServer = sqlServer;

    [Fact]
    public async Task CreateQuote_ValidRequest_Returns201CreatedWithLocationAndBody()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);

        var response = await host.Client.PostAsJsonAsync("/api/quotes", new CreateQuoteRequest { Author = "Ada Lovelace", Text = "A valid quote." });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        var body = await response.Content.ReadFromJsonAsync<QuoteResponse>(TestInfrastructure.Json);
        body.Should().NotBeNull();
        body!.Author.Should().Be("Ada Lovelace");
        body.Text.Should().Be("A valid quote.");
    }

    [Fact]
    public async Task CreateQuote_EmptyAuthorAndText_ReturnsValidationProblemDetailsWithFieldErrors()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);

        var response = await host.Client.PostAsJsonAsync("/api/quotes", new CreateQuoteRequest { Author = "", Text = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(TestInfrastructure.Json);
        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey("Author");
        problem.Errors.Should().ContainKey("Text");
    }

    [Fact]
    public async Task CreateQuote_AuthorExceeding200Chars_ReturnsValidationProblemForAuthorField()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);
        var tooLongAuthor = new string('a', 201);

        var response = await host.Client.PostAsJsonAsync("/api/quotes", new CreateQuoteRequest { Author = tooLongAuthor, Text = "A valid quote." });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(TestInfrastructure.Json);
        problem!.Errors.Should().ContainKey("Author");
    }

    [Fact]
    public async Task GetQuoteById_ExistingId_ReturnsOkWithQuote()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);
        var createResponse = await host.Client.PostAsJsonAsync("/api/quotes", new CreateQuoteRequest { Author = "Ada Lovelace", Text = "A valid quote." });
        var created = await createResponse.Content.ReadFromJsonAsync<QuoteResponse>(TestInfrastructure.Json);

        var response = await host.Client.GetAsync($"/api/quotes/{created!.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var fetched = await response.Content.ReadFromJsonAsync<QuoteResponse>(TestInfrastructure.Json);
        fetched!.Id.Should().Be(created.Id);
        fetched.Author.Should().Be("Ada Lovelace");
    }

    [Fact]
    public async Task GetQuoteById_NonExistentId_ReturnsNotFoundProblemDetails()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);

        var response = await host.Client.GetAsync("/api/quotes/999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestInfrastructure.Json);
        problem!.Title.Should().Be("Quote not found");
        problem.Status.Should().Be(404);
    }

    [Fact]
    public async Task GetQuotes_DefaultPaging_ReturnsAllCreatedQuotesOnFirstPage()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);
        await host.Client.PostAsJsonAsync("/api/quotes", new CreateQuoteRequest { Author = "Author One", Text = "Quote one." });
        await host.Client.PostAsJsonAsync("/api/quotes", new CreateQuoteRequest { Author = "Author Two", Text = "Quote two." });
        await host.Client.PostAsJsonAsync("/api/quotes", new CreateQuoteRequest { Author = "Author Three", Text = "Quote three." });

        var response = await host.Client.GetAsync("/api/quotes?page=1&size=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<QuoteResponse>>(TestInfrastructure.Json);
        page!.TotalCount.Should().Be(3);
        page.Items.Should().HaveCount(3);
        page.Page.Should().Be(1);
    }

    [Fact]
    public async Task GetQuotes_SizeExceeding100_IsClampedTo100InResponse()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);

        var response = await host.Client.GetAsync("/api/quotes?page=1&size=500");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<QuoteResponse>>(TestInfrastructure.Json);
        page!.Size.Should().Be(100);
    }

    [Fact]
    public async Task GetQuotes_NonPositivePage_DefaultsToPageOne()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);

        var response = await host.Client.GetAsync("/api/quotes?page=0&size=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<QuoteResponse>>(TestInfrastructure.Json);
        page!.Page.Should().Be(1);
    }

    [Fact]
    public async Task DeleteQuote_ExistingId_Returns204AndSubsequentGetReturns404()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);
        var createResponse = await host.Client.PostAsJsonAsync("/api/quotes", new CreateQuoteRequest { Author = "Ada Lovelace", Text = "A valid quote." });
        var created = await createResponse.Content.ReadFromJsonAsync<QuoteResponse>(TestInfrastructure.Json);

        var deleteResponse = await host.Client.DeleteAsync($"/api/quotes/{created!.Id}");
        var getResponse = await host.Client.GetAsync($"/api/quotes/{created.Id}");

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteQuote_NonExistentId_ReturnsNotFoundProblemDetails()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);

        var response = await host.Client.DeleteAsync("/api/quotes/999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestInfrastructure.Json);
        problem!.Title.Should().Be("Quote not found");
    }

    // This test used to assert the opposite, and was right to: IClock was registered
    // and overridable, but POST /api/quotes called an overload that read
    // DateTime.UtcNow directly, so the registration was decorative and the fake clock
    // was never consulted. The endpoint now resolves IClock and hands its instant to
    // Quote.Create, so overriding the clock in the test host changes what the endpoint
    // writes - end to end, through the real DI graph, EF, and SQL Server.
    //
    // Asserting the exact instant rather than a tolerance window is the point. A test
    // that says "close to now" would still pass if the clock were ignored again.
    [Fact]
    public async Task CreateQuote_WithClockOverridden_StampsCreatedAtFromTheInjectedClock()
    {
        var instant = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer, new FixedClock(instant));

        var response = await host.Client.PostAsJsonAsync("/api/quotes", new CreateQuoteRequest { Author = "Ada Lovelace", Text = "A valid quote." });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<QuoteResponse>(TestInfrastructure.Json);
        body!.CreatedAt.Should().Be(instant.UtcDateTime);
    }

    // The stored row, not just the response body. If CreatedAt were only being set on
    // the way out, the assertion above would still pass and the database would hold
    // the wrong value.
    [Fact]
    public async Task CreateQuote_WithClockOverridden_PersistsTheClockInstantAndReadsItBack()
    {
        var instant = new DateTimeOffset(1969, 7, 20, 20, 17, 0, TimeSpan.Zero);
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer, new FixedClock(instant));

        var created = await host.Client.PostAsJsonAsync("/api/quotes", new CreateQuoteRequest { Author = "Neil Armstrong", Text = "The Eagle has landed." });
        var createdBody = await created.Content.ReadFromJsonAsync<QuoteResponse>(TestInfrastructure.Json);

        var fetched = await host.Client.GetAsync($"/api/quotes/{createdBody!.Id}");

        fetched.StatusCode.Should().Be(HttpStatusCode.OK);
        var fetchedBody = await fetched.Content.ReadFromJsonAsync<QuoteResponse>(TestInfrastructure.Json);
        fetchedBody!.CreatedAt.Should().Be(instant.UtcDateTime);
    }
}
