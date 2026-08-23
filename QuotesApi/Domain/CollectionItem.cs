namespace QuotesApi.Domain;

/// <summary>
/// Value object: defined entirely by its values, immutable once constructed,
/// and mapped as an EF owned type. Two items with the same QuoteId and AddedAt
/// are the same item.
/// </summary>
public record CollectionItem
{
    public int QuoteId { get; init; }
    public DateTime AddedAt { get; init; }

    /// <param name="addedAt">
    /// Supplied by the caller rather than read from the system clock. See the
    /// note on Quote.Create for why the domain takes the instant and not IClock.
    /// </param>
    public CollectionItem(int quoteId, DateTimeOffset addedAt)
    {
        if (quoteId <= 0)
            throw new ArgumentException("Quote ID must be a positive integer.", nameof(quoteId));

        QuoteId = quoteId;
        AddedAt = addedAt.UtcDateTime;
    }

    private CollectionItem() { } // Required for EF Core
}
