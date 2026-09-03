namespace QuotesApi.Caching;

/// <summary>
/// Every cache key and tag this application uses, in one file.
/// </summary>
/// <remarks>
/// Centralised because a cache key built inline at the read site and an
/// invalidation built inline at the write site is exactly how a cache goes
/// stale: the two spellings drift apart and nothing fails, the data is just
/// quietly wrong. Reader and invalidator both come here.
///
/// The key includes every parameter that changes the answer. Leaving
/// previewSize out would let a request for 3 preview items be served the
/// cached response for 1 - the failure that looks like data corruption and is
/// actually a key that under-describes its value.
/// </remarks>
public static class CacheKeys
{
    /// <summary>
    /// Tag covering every cached collection-summary response, whatever its
    /// owner or preview size. One write invalidates all of them, because a
    /// write can change which collections exist and therefore the answer to
    /// the unfiltered query as well as the filtered one.
    /// </summary>
    public const string CollectionSummariesTag = "collection-summaries";

    public static readonly string[] CollectionSummariesTags = [CollectionSummariesTag];

    /// <param name="ownerId">
    /// Null means "all owners", which is a different question from any
    /// specific owner and so gets its own key rather than sharing one.
    /// </param>
    public static string CollectionSummaries(string? ownerId, int previewSize) =>
        $"collections:summaries:owner={ownerId ?? "*"}:preview={previewSize}";
}
