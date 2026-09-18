using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using QuotesApi.DTOs;

namespace Quotes.Tests.Integration;

[Collection(MsSqlCollection.Name)]
public class CollectionsEndpointsTests
{
    // CollectionsController returns the raw domain Collection, which has private setters and a
    // private parameterless constructor (not deserializable by System.Text.Json). These local
    // view records mirror only the JSON shape we need to assert against.
    private sealed record CollectionItemView(int QuoteId, DateTime AddedAt);
    private sealed record CollectionView(int Id, string Name, string OwnerId, List<CollectionItemView> Items);
    private sealed record CreateCollectionView(int Id);

    private readonly MsSqlContainerFixture _sqlServer;

    public CollectionsEndpointsTests(MsSqlContainerFixture sqlServer) => _sqlServer = sqlServer;

    [Fact]
    public async Task CreateCollection_ValidRequest_Returns201CreatedWithLocation()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);

        var response = await host.Client.PostAsJsonAsync("/api/collections", new CreateCollectionDto { Name = "My Collection", OwnerId = "owner-1" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        var body = await response.Content.ReadFromJsonAsync<CreateCollectionView>(TestInfrastructure.Json);
        body!.Id.Should().BePositive();
    }

    [Fact]
    public async Task CreateCollection_NameTooShort_ReturnsProblemDetailsBadRequest()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);

        var response = await host.Client.PostAsJsonAsync("/api/collections", new CreateCollectionDto { Name = "ab", OwnerId = "owner-1" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestInfrastructure.Json);
        problem!.Title.Should().Be("Bad Request");
    }

    [Fact]
    public async Task AddItemToCollection_ValidQuoteId_ReturnsOkWithUpdatedCollection()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);
        var createResponse = await host.Client.PostAsJsonAsync("/api/collections", new CreateCollectionDto { Name = "My Collection", OwnerId = "owner-1" });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateCollectionView>(TestInfrastructure.Json);

        var response = await host.Client.PostAsync($"/api/collections/{created!.Id}/items/42", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var getResponse = await host.Client.GetAsync($"/api/collections/{created.Id}");
        var updated = await getResponse.Content.ReadFromJsonAsync<CollectionView>(TestInfrastructure.Json);
        updated!.Items.Should().ContainSingle(i => i.QuoteId == 42);
    }

    [Fact]
    public async Task AddItemToCollection_NonExistentCollection_ReturnsNotFound()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);

        var response = await host.Client.PostAsync("/api/collections/999/items/1", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddItemToCollection_DuplicateQuoteId_ReturnsProblemDetailsBadRequest()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);
        var createResponse = await host.Client.PostAsJsonAsync("/api/collections", new CreateCollectionDto { Name = "My Collection", OwnerId = "owner-1" });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateCollectionView>(TestInfrastructure.Json);
        await host.Client.PostAsync($"/api/collections/{created!.Id}/items/42", null);

        var response = await host.Client.PostAsync($"/api/collections/{created.Id}/items/42", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestInfrastructure.Json);
        problem!.Title.Should().Be("Invariant Violation");
    }

    [Fact]
    public async Task RemoveItemFromCollection_ExistingItem_ReturnsOkWithUpdatedCollection()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);
        var createResponse = await host.Client.PostAsJsonAsync("/api/collections", new CreateCollectionDto { Name = "My Collection", OwnerId = "owner-1" });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateCollectionView>(TestInfrastructure.Json);
        await host.Client.PostAsync($"/api/collections/{created!.Id}/items/42", null);

        var response = await host.Client.DeleteAsync($"/api/collections/{created.Id}/items/42");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<CollectionView>(TestInfrastructure.Json);
        updated!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveItemFromCollection_NonExistentCollection_ReturnsNotFound()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);

        var response = await host.Client.DeleteAsync("/api/collections/999/items/1");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RemoveItemFromCollection_NonExistentItem_ReturnsProblemDetailsBadRequest()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);
        var createResponse = await host.Client.PostAsJsonAsync("/api/collections", new CreateCollectionDto { Name = "My Collection", OwnerId = "owner-1" });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateCollectionView>(TestInfrastructure.Json);

        var response = await host.Client.DeleteAsync($"/api/collections/{created!.Id}/items/999");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestInfrastructure.Json);
        problem!.Title.Should().Be("Bad Request");
    }

    [Fact]
    public async Task GetCollectionById_ExistingId_ReturnsOkWithCollection()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);
        var createResponse = await host.Client.PostAsJsonAsync("/api/collections", new CreateCollectionDto { Name = "My Collection", OwnerId = "owner-1" });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateCollectionView>(TestInfrastructure.Json);

        var response = await host.Client.GetAsync($"/api/collections/{created!.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var fetched = await response.Content.ReadFromJsonAsync<CollectionView>(TestInfrastructure.Json);
        fetched!.Name.Should().Be("My Collection");
    }

    [Fact]
    public async Task GetCollectionById_NonExistentId_ReturnsNotFound()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);

        var response = await host.Client.GetAsync("/api/collections/999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The test that should have existed since Day 25 and did not.
    /// </summary>
    /// <remarks>
    /// Every test above this one uses <c>host.Client</c>, which CreateFreshHost
    /// signs in before handing it over. That is convenient and it is exactly why
    /// nobody noticed that CollectionsController carried no authorization at
    /// all: a suite where every caller has a token cannot tell a protected
    /// endpoint from an unprotected one.
    ///
    /// Asserted across every verb rather than on one endpoint, because the fix
    /// is <c>[Authorize]</c> on the class and a single-endpoint test would pass
    /// just as well against an attribute on a single action - which is the
    /// version of the fix that lets the eighth action be added unprotected.
    /// </remarks>
    [Fact]
    public async Task Collections_RejectAnAnonymousCaller_OnEveryVerb()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);

        var createResponse = await host.Client.PostAsJsonAsync(
            "/api/collections", new CreateCollectionDto { Name = "My Collection", OwnerId = "owner-1" });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateCollectionView>(TestInfrastructure.Json);

        var anonymous = host.AnonymousClient();

        (await anonymous.GetAsync("/api/collections")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "GET / returns every collection in the database");

        (await anonymous.GetAsync("/api/collections/summaries")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        (await anonymous.GetAsync($"/api/collections/{created!.Id}")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        (await anonymous.PostAsJsonAsync(
            "/api/collections", new CreateCollectionDto { Name = "Theirs", OwnerId = "owner-2" })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        (await anonymous.PostAsync($"/api/collections/{created.Id}/items/42", null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        (await anonymous.DeleteAsync($"/api/collections/{created.Id}/items/42")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "an anonymous DELETE was reachable until Day 32");
    }

    /// <summary>
    /// Documents a hole that is still open, so that closing it is a failing
    /// test rather than a discovery.
    /// </summary>
    /// <remarks>
    /// This is a characterisation test: it asserts what the code does today,
    /// not what it should do. <c>[Authorize]</c> fixed "anyone at all"; it did
    /// not fix "anyone signed in", because ownership is still a string the
    /// caller supplies rather than a claim on their token. Two different users
    /// can therefore see and modify each other's collections.
    ///
    /// Written down as a test rather than only as a comment for one reason:
    /// when somebody derives the owner from ClaimsPrincipal, this test goes red
    /// and tells them the behaviour they just changed was known and deliberate
    /// rather than accidental. A comment would not have stopped them wondering.
    ///
    /// The fix belongs with the read-model contract and the Angular client that
    /// sends ownerId, and is scoped in the class remarks on
    /// CollectionsController.
    /// </remarks>
    [Fact]
    public async Task Collections_StillLetOneSignedInUserSeeAnothersData_KnownGap()
    {
        using var host = await TestInfrastructure.CreateFreshHost(_sqlServer);

        var createResponse = await host.Client.PostAsJsonAsync(
            "/api/collections", new CreateCollectionDto { Name = "Alice's shelf", OwnerId = "alice" });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateCollectionView>(TestInfrastructure.Json);

        var (mallory, _) = await host.SignUpAsync("mallory@example.com");

        var response = await mallory.GetAsync($"/api/collections/{created!.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "known gap: ownership is a caller-supplied ownerId, not a claim - see the class remarks "
            + "on CollectionsController. When this starts returning 404, the gap has been closed and "
            + "this test should be replaced with its opposite.");

        var fetched = await response.Content.ReadFromJsonAsync<CollectionView>(TestInfrastructure.Json);
        fetched!.OwnerId.Should().Be("alice", "and mallory can read whose it is");
    }
}