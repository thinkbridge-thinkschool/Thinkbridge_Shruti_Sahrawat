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

    // ---- ownership (Day 19) -------------------------------------------------
    //
    // Every test above uses host.Client, which CreateFreshHost signs in as an
    // ordinary user. These are the ones about who may see and delete what, so
    // they create their own identities explicitly.

    [Fact]
    public async Task GetQuotes_WithoutAToken_Returns401()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);

        var response = await host.AnonymousClient().GetAsync("/api/quotes?page=1&size=10");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateQuote_WithoutAToken_Returns401()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);

        var response = await host.AnonymousClient().PostAsJsonAsync(
            "/api/quotes", new CreateQuoteRequest { Author = "Anon", Text = "Should not be stored." });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // The owner comes from the token, not the body - CreateQuoteRequest has no
    // owner field for a client to set. This is what proves the endpoint fills
    // it in rather than leaving every quote un-owned.
    [Fact]
    public async Task CreateQuote_StampsTheCallerAsTheOwner()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);
        var (ada, adaUser) = await host.SignUpAsync("ada@example.com");

        var response = await ada.PostAsJsonAsync(
            "/api/quotes", new CreateQuoteRequest { Author = "Ada Lovelace", Text = "A valid quote." });

        var body = await response.Content.ReadFromJsonAsync<QuoteResponse>(TestInfrastructure.Json);
        body!.OwnerId.Should().Be(adaUser.Id);
    }

    [Fact]
    public async Task GetQuotes_ForAnOrdinaryUser_ReturnsOnlyTheirOwnQuotes()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);
        var (ada, _) = await host.SignUpAsync("ada@example.com");
        var (grace, _) = await host.SignUpAsync("grace@example.com");

        await ada.PostAsJsonAsync("/api/quotes", new CreateQuoteRequest { Author = "Ada", Text = "Ada's own quote." });
        await grace.PostAsJsonAsync("/api/quotes", new CreateQuoteRequest { Author = "Grace", Text = "Grace's own quote." });

        var response = await ada.GetAsync("/api/quotes?page=1&size=10");
        var page = await response.Content.ReadFromJsonAsync<PagedResult<QuoteResponse>>(TestInfrastructure.Json);

        // TotalCount as well as Items: a listing that filtered the rows but
        // counted the whole table would page over somebody else's data and
        // report a total that describes it.
        page!.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle().Which.Text.Should().Be("Ada's own quote.");
    }

    // 404 rather than 403, deliberately. A 403 would confirm the quote exists,
    // which turns sequential ids into a way to count how much data other people
    // have. From outside, "not there" and "not yours" are indistinguishable.
    [Fact]
    public async Task GetQuoteById_SomeoneElsesQuote_Returns404()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);
        var (ada, _) = await host.SignUpAsync("ada@example.com");
        var (grace, _) = await host.SignUpAsync("grace@example.com");

        var created = await grace.PostAsJsonAsync("/api/quotes", new CreateQuoteRequest { Author = "Grace", Text = "Grace's own quote." });
        var graceQuote = await created.Content.ReadFromJsonAsync<QuoteResponse>(TestInfrastructure.Json);

        var response = await ada.GetAsync($"/api/quotes/{graceQuote!.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteQuote_SomeoneElsesQuote_Returns404AndLeavesItInPlace()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);
        var (ada, _) = await host.SignUpAsync("ada@example.com");
        var (grace, _) = await host.SignUpAsync("grace@example.com");

        var created = await grace.PostAsJsonAsync("/api/quotes", new CreateQuoteRequest { Author = "Grace", Text = "Grace's own quote." });
        var graceQuote = await created.Content.ReadFromJsonAsync<QuoteResponse>(TestInfrastructure.Json);

        var deleteResponse = await ada.DeleteAsync($"/api/quotes/{graceQuote!.Id}");

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The status code alone would pass even if the row had been deleted and
        // the endpoint merely lied about it. Grace asking for her own quote is
        // what proves it is still there.
        var stillThere = await grace.GetAsync($"/api/quotes/{graceQuote.Id}");
        stillThere.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetQuotes_AsAnAdmin_ReturnsEveryUsersQuotes()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer, null, "boss@example.com");
        var (boss, bossUser) = await host.SignUpAsync("boss@example.com");
        var (ada, _) = await host.SignUpAsync("ada@example.com");

        bossUser.Role.Should().Be(Roles.Admin, "the address was listed in Auth:AdminEmails");

        await ada.PostAsJsonAsync("/api/quotes", new CreateQuoteRequest { Author = "Ada", Text = "Ada's own quote." });
        await boss.PostAsJsonAsync("/api/quotes", new CreateQuoteRequest { Author = "Boss", Text = "The admin's own quote." });

        var response = await boss.GetAsync("/api/quotes?page=1&size=10");
        var page = await response.Content.ReadFromJsonAsync<PagedResult<QuoteResponse>>(TestInfrastructure.Json);

        page!.Items.Select(q => q.Text).Should().Contain(new[] { "Ada's own quote.", "The admin's own quote." });
    }

    [Fact]
    public async Task DeleteQuote_AsAnAdmin_CanDeleteSomeoneElsesQuote()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer, null, "boss@example.com");
        var (boss, _) = await host.SignUpAsync("boss@example.com");
        var (ada, _) = await host.SignUpAsync("ada@example.com");

        var created = await ada.PostAsJsonAsync("/api/quotes", new CreateQuoteRequest { Author = "Ada", Text = "Ada's own quote." });
        var adaQuote = await created.Content.ReadFromJsonAsync<QuoteResponse>(TestInfrastructure.Json);

        var deleteResponse = await boss.DeleteAsync($"/api/quotes/{adaQuote!.Id}");

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var adaLooksForIt = await ada.GetAsync($"/api/quotes/{adaQuote.Id}");
        adaLooksForIt.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // The role is decided by the server from configuration. This is the test
    // that would fail if someone ever added a role field to RegisterRequest.
    [Fact]
    public async Task Register_ForAnAddressNotListedAsAdmin_CreatesAnOrdinaryUser()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer, null, "boss@example.com");

        var (_, user) = await host.SignUpAsync("someone-else@example.com");

        user.Role.Should().Be(Roles.User);
    }
}
