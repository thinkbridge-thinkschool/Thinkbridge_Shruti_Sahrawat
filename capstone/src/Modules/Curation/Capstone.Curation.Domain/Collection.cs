using Capstone.Curation.Domain.Events;
using Capstone.SharedKernel;

namespace Capstone.Curation.Domain;

/// <summary>
/// The core aggregate: a curated, ordered set of quotes that a curator can
/// publish to their followers.
/// </summary>
/// <remarks>
/// <b>What the consistency boundary actually is.</b> A collection and its items
/// are one unit - the "no more than 50 items" and "no duplicates" rules cannot
/// be enforced without seeing all of them at once, so they must load and save
/// together. Quotes are deliberately outside it: this aggregate holds
/// <see cref="QuoteId"/> and never a quote's text, so publishing a collection
/// does not lock, load, or version anything the Catalog module owns.
///
/// <b>What this aggregate cannot check, and does not pretend to.</b> Whether
/// the referenced quotes actually exist is a question only Catalog can answer,
/// and an aggregate that reached out to ask would be doing I/O inside a domain
/// rule. That check lives in the application handler, before Publish is called.
/// The rule of thumb this follows: an aggregate enforces what it can see, and
/// anything requiring another context's data is a use-case concern.
///
/// <b>Publishing freezes the collection.</b> Once published, items cannot be
/// added or removed - a curator must unpublish first. This is a real product
/// decision with a real cost: it makes fixing a typo in a popular collection a
/// two-step operation that briefly withdraws it from followers' feeds. The
/// alternative is versioning - publish creates an immutable snapshot and the
/// draft stays editable - which is better, and is a larger piece of work than
/// a kickoff should be scaffolding. Freezing is the honest interim: it keeps
/// "what followers saw" and "what the curator has" from silently diverging,
/// which is the failure that would be hardest to unpick later.
/// </remarks>
public sealed class Collection : AggregateRoot<CollectionId>
{
    public const int MaxItems = 50;
    public const int MinNameLength = 3;
    public const int MaxNameLength = 80;

    private readonly List<CollectionItem> _items = [];

    public CuratorId CuratorId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public CollectionStatus Status { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public IReadOnlyList<CollectionItem> Items => _items;

    private Collection()
    {
        // EF Core materialisation only.
    }

    private Collection(CollectionId id, CuratorId curatorId, string name)
    {
        Id = id;
        CuratorId = curatorId;
        Status = CollectionStatus.Draft;
        Rename(name);
    }

    /// <summary>
    /// The only way to bring a collection into existence.
    /// </summary>
    public static Collection Start(CuratorId curatorId, string name)
        => new(CollectionId.New(), curatorId, name);

    public void Rename(string name)
    {
        EnsureDraft("renamed");

        var trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length is < MinNameLength or > MaxNameLength)
        {
            throw new DomainException(
                $"Collection name must be between {MinNameLength} and {MaxNameLength} characters.");
        }

        Name = trimmed;
    }

    public void AddItem(QuoteId quoteId, DateTimeOffset addedAt)
    {
        EnsureDraft("added to");

        if (_items.Count >= MaxItems)
        {
            throw new DomainException($"A collection cannot hold more than {MaxItems} quotes.");
        }

        if (_items.Any(item => item.QuoteId == quoteId))
        {
            throw new DomainException($"Quote {quoteId} is already in this collection.");
        }

        _items.Add(new CollectionItem(quoteId, addedAt));
    }

    public void RemoveItem(QuoteId quoteId)
    {
        EnsureDraft("removed from");

        var index = _items.FindIndex(item => item.QuoteId == quoteId);

        if (index < 0)
        {
            throw new DomainException($"Quote {quoteId} is not in this collection.");
        }

        _items.RemoveAt(index);
    }

    /// <summary>
    /// Makes the collection visible to the curator's followers and records the
    /// fact for anything downstream that cares.
    /// </summary>
    public void Publish(DateTimeOffset publishedAt)
    {
        if (Status is CollectionStatus.Published)
        {
            // Not a no-op on purpose. Publishing twice almost always means the
            // caller lost track of state, and a silent success would let a
            // duplicate fan-out look like it worked.
            throw new DomainException("This collection is already published.");
        }

        if (_items.Count == 0)
        {
            throw new DomainException("A collection needs at least one quote before it can be published.");
        }

        Status = CollectionStatus.Published;
        PublishedAt = publishedAt;

        Raise(new CollectionPublished(
            Id,
            CuratorId,
            Name,
            _items.Select(item => item.QuoteId).ToArray(),
            publishedAt));
    }

    /// <summary>
    /// Withdraws the collection from followers' feeds and makes it editable again.
    /// </summary>
    public void Unpublish()
    {
        if (Status is CollectionStatus.Draft)
        {
            throw new DomainException("This collection is not published.");
        }

        Status = CollectionStatus.Draft;
        PublishedAt = null;
    }

    private void EnsureDraft(string attemptedChange)
    {
        if (Status is CollectionStatus.Published)
        {
            throw new DomainException(
                $"A published collection cannot be {attemptedChange}. Unpublish it first.");
        }
    }
}
