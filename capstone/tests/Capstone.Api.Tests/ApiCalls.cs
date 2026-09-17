using System.Net.Http.Json;
using System.Text.Json;

namespace Capstone.Api.Tests;

/// <summary>
/// The API as a test sees it: an HTTP client and JSON, and nothing else.
/// </summary>
/// <remarks>
/// Every helper here goes over the wire. None of them reach into the host's
/// service provider to shortcut a step, which is a rule rather than a
/// preference - a test that seeds state by resolving a repository is asserting
/// against a database it wrote to directly, and will keep passing after the
/// endpoint that was supposed to write it stops working.
///
/// The one place this project touches the container at all is
/// <see cref="MigrationTests"/>, which is about the schema rather than about
/// behaviour and has no HTTP surface to ask.
/// </remarks>
internal static class ApiCalls
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<Guid> StartCollectionAsync(
        this HttpClient client, string curatorId, string name)
    {
        var response = await client.PostAsJsonAsync(
            "/api/collections", new { curatorId, name });

        // Loud, and here rather than at the assertion. A setup step that failed
        // quietly surfaces three lines later as an unexplained 404 on the call
        // the test is actually about, and sends whoever debugs it to the wrong
        // endpoint.
        await EnsureSucceeded(response, $"start a collection for {curatorId}");

        var body = await response.Content.ReadFromJsonAsync<StartedCollection>(Json);

        return body?.CollectionId
            ?? throw new InvalidOperationException("POST /api/collections returned no collectionId.");
    }

    public static Task<HttpResponseMessage> AddItemAsync(
        this HttpClient client, Guid collectionId, int quoteId)
        => client.PostAsJsonAsync($"/api/collections/{collectionId}/items", new { quoteId });

    public static Task<HttpResponseMessage> PublishAsync(
        this HttpClient client, Guid collectionId, string curatorId)
        => client.PostAsJsonAsync($"/api/collections/{collectionId}/publish", new { curatorId });

    public static async Task FollowAsync(this HttpClient client, string curatorId, string followerId)
    {
        var response = await client.PostAsJsonAsync("/api/follows", new { curatorId, followerId });

        await EnsureSucceeded(response, $"make {followerId} follow {curatorId}");
    }

    public static async Task<IReadOnlyList<OutboxRow>> OutboxAsync(this HttpClient client)
        => await client.GetFromJsonAsync<List<OutboxRow>>("/api/outbox", Json) ?? [];

    public static async Task<IReadOnlyList<FeedRow>> FeedAsync(this HttpClient client, string followerId)
        => await client.GetFromJsonAsync<List<FeedRow>>($"/api/feed/{followerId}", Json) ?? [];

    /// <summary>The <c>{ "error": "..." }</c> body the DomainException middleware writes.</summary>
    /// <remarks>
    /// Reads the body as a string and deserialises that, rather than calling
    /// ReadFromJsonAsync directly, so this can be called more than once on the
    /// same response. ReadFromJsonAsync reads the content stream and disposes
    /// it; a second call gets ObjectDisposedException: Cannot access a closed
    /// Stream, which names the symptom and not the cause and cost twenty
    /// minutes the first time.
    ///
    /// Asserting twice about one response is a normal thing for a test to want
    /// - compare it to another, then pin its exact text - so the helper is the
    /// right place to absorb that rather than every caller remembering to read
    /// into a local first.
    /// </remarks>
    public static async Task<string> ErrorMessageAsync(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        return JsonSerializer.Deserialize<ErrorBody>(body, Json)?.Error ?? string.Empty;
    }

    private static async Task EnsureSucceeded(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();

        throw new InvalidOperationException(
            $"Test setup could not {what}: {(int)response.StatusCode} {response.StatusCode}. {body}");
    }

    private sealed record StartedCollection(Guid CollectionId);

    private sealed record ErrorBody(string Error);
}

internal sealed record OutboxRow(
    Guid MessageId, string EventType, DateTimeOffset OccurredAt, DateTimeOffset? SentAt, bool Delivered);

internal sealed record FeedRow(
    Guid CollectionId, string CuratorId, string Name, DateTimeOffset PublishedAt);
