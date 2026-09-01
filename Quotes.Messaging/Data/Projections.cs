namespace Quotes.Messaging.Data;

/// <summary>
/// The search-indexer's own copy of a quote. A consumer owning its own store,
/// rather than reaching into the API's database, is the point: the two can be
/// deployed, scaled and rebuilt independently, and a bad index can be dropped
/// and replayed from the topic without touching the source of truth.
/// </summary>
public sealed class IndexedQuote
{
    public int QuoteId { get; set; }
    public string Author { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset IndexedAt { get; set; }
}

/// <summary>
/// The audit-log subscription's append-only record. It records every event
/// type, including the ones the search indexer's filter excludes.
/// </summary>
public sealed class AuditEntry
{
    public int Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int QuoteId { get; set; }
    public string Detail { get; set; } = string.Empty;
    public DateTimeOffset RecordedAt { get; set; }
}
