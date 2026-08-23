namespace QuotesApi.Domain;

/// <summary>
/// Aggregate root and consistency boundary. Every mutation to the items inside
/// goes through this type, which throws rather than letting a caller reach in
/// and break an invariant.
/// </summary>
public class Collection
{
    public const int MaxItems = 50;
    public const int MinNameLength = 3;
    public const int MaxNameLength = 80;

    private readonly List<CollectionItem> _items = new();

    public int Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string OwnerId { get; private set; } = string.Empty;
    public IReadOnlyCollection<CollectionItem> Items => _items.AsReadOnly();

    public Collection(string name, string ownerId)
    {
        SetName(name);
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new InvalidOperationException("Owner ID cannot be empty.");
        OwnerId = ownerId;
    }

    private Collection() { } // Required for EF Core

    public void SetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Trim().Length < MinNameLength
            || name.Trim().Length > MaxNameLength)
        {
            throw new InvalidOperationException(
                $"Collection name must be between {MinNameLength} and {MaxNameLength} characters.");
        }

        Name = name.Trim();
    }

    /// <param name="addedAt">
    /// When the quote was added. Passed in by the application layer, which owns
    /// the IClock, so the aggregate itself never reads the ambient clock and a
    /// test can pin the timestamp to any instant it likes.
    /// </param>
    public void AddItem(int quoteId, DateTimeOffset addedAt)
    {
        if (_items.Count >= MaxItems)
            throw new InvalidOperationException($"Collection cannot contain more than {MaxItems} quotes.");

        if (_items.Any(x => x.QuoteId == quoteId))
            throw new InvalidOperationException($"Quote with ID {quoteId} already exists in this collection.");

        _items.Add(new CollectionItem(quoteId, addedAt));
    }

    public void RemoveItem(int quoteId)
    {
        var existing = _items.FirstOrDefault(x => x.QuoteId == quoteId);
        if (existing == null)
            throw new InvalidOperationException($"Quote with ID {quoteId} was not found.");

        _items.Remove(existing);
    }
}
