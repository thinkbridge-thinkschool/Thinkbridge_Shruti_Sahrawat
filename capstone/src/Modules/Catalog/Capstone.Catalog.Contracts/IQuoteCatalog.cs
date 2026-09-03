namespace Capstone.Catalog.Contracts;

/// <summary>
/// Everything the rest of the system is allowed to know about quotes.
/// </summary>
/// <remarks>
/// This project is Catalog's published language, and it is the only Catalog
/// assembly another module may reference. That constraint is not a convention
/// here - Capstone.ArchitectureTests fails the build if any module references
/// Catalog.Infrastructure, so the boundary is enforced by CI rather than by
/// whoever reviews the pull request.
///
/// Deliberately not a repository. Curation does not get to page, filter, or
/// join across quotes; it gets to ask the two questions it actually has. A
/// contract shaped like a database is one the supplier can never change.
/// </remarks>
public interface IQuoteCatalog
{
    /// <summary>
    /// Which of these quote ids do not exist (or are deleted)? An empty result
    /// means all of them are usable.
    /// </summary>
    /// <remarks>
    /// Returns the missing ones rather than a bool, because the caller's next
    /// move is telling a user which quotes are the problem, and a bool forces
    /// a second round trip to find out.
    /// </remarks>
    Task<IReadOnlyList<int>> FindMissingAsync(
        IReadOnlyList<int> quoteIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<QuoteSummary>> GetSummariesAsync(
        IReadOnlyList<int> quoteIds, CancellationToken cancellationToken);
}

/// <summary>What a quote looks like to everyone who is not Catalog.</summary>
public sealed record QuoteSummary(int QuoteId, string Author, string Text);
