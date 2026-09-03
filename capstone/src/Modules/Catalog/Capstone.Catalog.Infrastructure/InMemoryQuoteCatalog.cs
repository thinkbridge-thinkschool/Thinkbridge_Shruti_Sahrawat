using Capstone.Catalog.Contracts;

namespace Capstone.Catalog.Infrastructure;

/// <summary>
/// Scaffold implementation of <see cref="IQuoteCatalog"/>.
/// </summary>
/// <remarks>
/// A stub, and labelled as one rather than dressed up. What it is standing in
/// for is decided: the real implementation reads the existing QuotesApi quote
/// tables, because Catalog is the one context this capstone does not need to
/// rebuild - it already exists and works. That makes Catalog the supplier in a
/// customer/supplier relationship, and this assembly the seam where the
/// existing schema gets translated into the published language above.
///
/// The reason a stub is honest here and would not be in Curation: Catalog's
/// behaviour is already proven by 54 integration tests in the main solution.
/// Curation's is not, which is why its aggregate is real code with real tests
/// and this is fifteen lines of dictionary.
/// </remarks>
public sealed class InMemoryQuoteCatalog(IReadOnlyDictionary<int, QuoteSummary> quotes) : IQuoteCatalog
{
    public Task<IReadOnlyList<int>> FindMissingAsync(
        IReadOnlyList<int> quoteIds, CancellationToken cancellationToken)
    {
        IReadOnlyList<int> missing = quoteIds.Where(id => !quotes.ContainsKey(id)).ToArray();
        return Task.FromResult(missing);
    }

    public Task<IReadOnlyList<QuoteSummary>> GetSummariesAsync(
        IReadOnlyList<int> quoteIds, CancellationToken cancellationToken)
    {
        IReadOnlyList<QuoteSummary> found = quoteIds
            .Where(quotes.ContainsKey)
            .Select(id => quotes[id])
            .ToArray();

        return Task.FromResult(found);
    }
}
