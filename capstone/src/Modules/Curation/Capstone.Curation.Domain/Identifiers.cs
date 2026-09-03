using Capstone.SharedKernel;

namespace Capstone.Curation.Domain;

/// <summary>
/// The identity of a collection, generated in the domain rather than by the
/// database.
/// </summary>
/// <remarks>
/// This is a deliberate correction of something the existing QuotesApi got
/// wrong, and Day 20 paid for. There, Collection.Id is a database-generated
/// int, so an aggregate does not know who it is until after it is saved - which
/// is precisely why the outbox write had to call SaveChangesAsync twice: once
/// to get the id, then again to write the outbox row that needed it. An
/// identity the domain mints itself removes that ordering constraint entirely,
/// and the whole write becomes one SaveChanges in one transaction.
///
/// Version 7 rather than a random Guid because v7 is time-ordered, so inserts
/// land at the end of the clustered index instead of scattering page splits
/// across it - the same index-locality reasoning as Day 8, applied to the key
/// itself.
/// </remarks>
public readonly record struct CollectionId(Guid Value)
{
    public static CollectionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Who owns a collection. A wrapper rather than a raw string so that a curator
/// id and a quote id and a collection name - all strings once you erase the
/// type - cannot be passed to each other's parameters by mistake.
/// </summary>
public readonly record struct CuratorId
{
    public string Value { get; }

    public CuratorId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException("Curator id cannot be empty.");
        }

        Value = value.Trim();
    }

    public override string ToString() => Value;
}

/// <summary>
/// A reference to a quote owned by the Catalog module.
/// </summary>
/// <remarks>
/// Curation stores the id and nothing else. It does not hold the author, the
/// text, or any other Catalog field, because duplicating another context's data
/// means owning the question of what happens when that data changes. Referencing
/// by identity keeps the answer simple: ask Catalog.
/// </remarks>
public readonly record struct QuoteId
{
    public int Value { get; }

    public QuoteId(int value)
    {
        if (value <= 0)
        {
            throw new DomainException("Quote id must be a positive integer.");
        }

        Value = value;
    }

    public override string ToString() => Value.ToString();
}
