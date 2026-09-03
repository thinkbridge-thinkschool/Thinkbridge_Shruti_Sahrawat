namespace Capstone.Curation.Domain;

/// <summary>
/// A quote's place in a collection. A value object: no identity of its own,
/// defined entirely by which quote it points at and when it was put there.
/// </summary>
/// <param name="AddedAt">
/// Supplied by the application layer, which owns the clock. The domain never
/// reads the ambient time - the rule Day 2 established, and the reason every
/// timestamp assertion in this codebase can be exact rather than "within five
/// seconds".
/// </param>
/// <remarks>
/// A record class rather than a record struct, and not by accident: EF Core
/// can only map owned entity types that are reference types, and this is
/// destined to be an owned collection on Collection exactly as CollectionItem
/// already is in QuotesApi. Choosing the struct would have read better in the
/// domain and then forced a rewrite at the first persistence mapping.
/// </remarks>
public sealed record CollectionItem(QuoteId QuoteId, DateTimeOffset AddedAt);
