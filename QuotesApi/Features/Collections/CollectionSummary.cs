namespace QuotesApi.Features.Collections;

// Read model. Shaped for the "my collections" screen, not for the database.
//
// The aggregate stores CollectionItem as { QuoteId, AddedAt } - just a foreign
// key. A screen listing collections needs to show what is actually in them,
// which means the quote text and author from a different table. That join is
// the reason this type exists rather than reusing the domain model.
public sealed record CollectionSummary(
    int Id,
    string Name,
    string OwnerId,
    int ItemCount,
    DateTime? MostRecentlyAdded,
    IReadOnlyList<CollectionPreviewItem> Preview);

public sealed record CollectionPreviewItem(
    int QuoteId,
    string Author,
    string Text,
    DateTime AddedAt);