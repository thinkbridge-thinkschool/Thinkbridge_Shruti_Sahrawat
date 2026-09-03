using Capstone.SharedKernel;

namespace Capstone.Curation.Domain.Events;

/// <summary>
/// A curator made a collection visible to their followers.
/// </summary>
/// <remarks>
/// Carries the quote ids, not the quote text. The temptation is to fatten this
/// event with everything a subscriber might want so it never has to ask
/// anybody anything - and that is how one module ends up owning a stale copy
/// of another module's data with no answer for what happens when the original
/// changes. A subscriber that needs quote text asks Catalog for it, and gets
/// today's answer rather than the answer as of publish time.
///
/// The counter-argument is real and worth naming: a feed that renders a
/// deleted quote's text differently tomorrow than it did today is arguably
/// wrong too, and the fix for that is an explicit snapshot the curator owns -
/// a different feature, deliberately not this one.
/// </remarks>
public sealed record CollectionPublished(
    CollectionId CollectionId,
    CuratorId CuratorId,
    string Name,
    IReadOnlyList<QuoteId> QuoteIds,
    DateTimeOffset OccurredAt) : IDomainEvent;
