using QuotesApi.Models;

namespace QuotesApi.Repositories;

public interface IQuoteRepository
{
    /// <param name="ownerId">
    /// Whose quotes to return: a user id to restrict the page to that user's
    /// own rows, or null for "every quote, whoever owns it".
    /// </param>
    /// <remarks>
    /// The filter is a repository parameter rather than something the endpoint
    /// applies to the results afterwards, because those two are not the same
    /// query. Filtering after the fact would page over everyone's rows and
    /// then discard most of them - a user with three quotes would get an
    /// almost-empty first page, a TotalCount describing somebody else's data,
    /// and a pager that disagreed with what was on screen.
    /// </remarks>
    Task<(IReadOnlyList<Quote> Items, int TotalCount)> GetPagedAsync(int page, int size, int? ownerId, CancellationToken ct);

    Task<Quote?> GetByIdAsync(int id, CancellationToken ct);
    Task<Quote> AddAsync(Quote quote, CancellationToken ct);
    Task<bool> DeleteAsync(int id, CancellationToken ct);
}
