using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Repositories;

public class QuoteRepository : IQuoteRepository
{
    private readonly QuotesDbContext _context;
    private readonly ILogger<QuoteRepository> _logger;

    public QuoteRepository(QuotesDbContext context, ILogger<QuoteRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<(IReadOnlyList<Quote> Items, int TotalCount)> GetPagedAsync(int page, int size, int? ownerId, CancellationToken ct)
    {
        _logger.LogInformation("Fetching quotes page {Page} size {Size} owner {OwnerId}", page, size, ownerId);

        // One query shape, built once and used for both the count and the page,
        // so the two can never disagree about which rows they are describing.
        // Counting the whole table and then paging a filtered set is the classic
        // way to end up with "Page 1 of 4" above a list of three rows.
        var query = _context.Quotes.AsQueryable();
        if (ownerId is not null)
        {
            query = query.Where(q => q.OwnerId == ownerId);
        }

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .OrderBy(q => q.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);
        return (items, totalCount);
    }

    public async Task<Quote?> GetByIdAsync(int id, CancellationToken ct)
    {
        _logger.LogInformation("Fetching quote {Id}", id);
        return await _context.Quotes.FindAsync(new object[] { id }, ct);
    }

    public async Task<Quote> AddAsync(Quote quote, CancellationToken ct)
    {
        _context.Quotes.Add(quote);
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Created quote {Id}", quote.Id);
        return quote;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        var quote = await _context.Quotes.FindAsync(new object[] { id }, ct);
        if (quote is null) return false;

        _context.Quotes.Remove(quote);
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted quote {Id}", id);
        return true;
    }
}