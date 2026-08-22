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
}