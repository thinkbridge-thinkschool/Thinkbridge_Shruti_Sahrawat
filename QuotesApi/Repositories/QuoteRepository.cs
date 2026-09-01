using Microsoft.EntityFrameworkCore;
using Quotes.Messaging.Contracts;
using Quotes.Messaging.Publishing;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Services;

namespace QuotesApi.Repositories;

public class QuoteRepository : IQuoteRepository
{
    private readonly QuotesDbContext _context;
    private readonly ILogger<QuoteRepository> _logger;
    private readonly IClock _clock;

    public QuoteRepository(QuotesDbContext context, ILogger<QuoteRepository> logger, IClock clock)
    {
        _context = context;
        _logger = logger;
        _clock = clock;
    }

    public async Task<(IReadOnlyList<Quote> Items, int TotalCount)> GetPagedAsync(int page, int size, int? ownerId, string? authorFilter, CancellationToken ct)
    {
        _logger.LogInformation(
            "Fetching quotes page {Page} size {Size} owner {OwnerId} author {AuthorFilter}",
            page, size, ownerId, authorFilter);

        // One query shape, built once and used for both the count and the page,
        // so the two can never disagree about which rows they are describing.
        // Counting the whole table and then paging a filtered set is the classic
        // way to end up with "Page 1 of 4" above a list of three rows.
        var query = _context.Quotes.AsQueryable();
        if (ownerId is not null)
        {
            query = query.Where(q => q.OwnerId == ownerId);
        }

        if (!string.IsNullOrWhiteSpace(authorFilter))
        {
            // ToLower() on both sides rather than EF.Functions.Like or
            // string.Contains(x, StringComparison.OrdinalIgnoreCase): the
            // former only translates on SQL Server, the latter is provider-
            // specific too, and this app runs on SQLite locally and SQL
            // Server in integration tests - a filter that only worked
            // against one of them would look fine on a laptop and 500 the
            // first time it hit the other.
            var needle = authorFilter.Trim().ToLower();
            query = query.Where(q => q.Author.ToLower().Contains(needle));
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

    /// <remarks>
    /// This is the write side of Day 20's outbox: the insert and the outbox
    /// row that announces it happen in one database transaction, so a crash
    /// between them cannot leave a quote nobody will ever hear about.
    ///
    /// It is deliberately <em>two</em> SaveChanges calls, not one, wrapped in
    /// an explicit transaction rather than relying on a single SaveChanges to
    /// be "the" transaction the way QuoteEventDispatcher's ledger write does.
    /// The outbox payload needs <c>quote.Id</c> - both in the JSON body and in
    /// the deterministic message id - and that value does not exist until the
    /// insert has actually run against the database; SQLite and SQL Server
    /// both assign it on execution, not when the entity is staged. Committing
    /// only once both writes have succeeded is what makes the two atomic here,
    /// not the number of round trips: if the second SaveChanges throws for any
    /// reason, the transaction is disposed without a commit and the first
    /// insert rolls back with it, so the quote never exists without the
    /// outbox row that is supposed to announce it. See
    /// AddAsync_WhenTheOutboxInsertFails_RollsBackTheQuoteToo for the test
    /// that forces exactly this and checks the quote table from a separate,
    /// untainted connection afterwards.
    /// </remarks>
    public async Task<Quote> AddAsync(Quote quote, CancellationToken ct)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(ct);

        _context.Quotes.Add(quote);
        await _context.SaveChangesAsync(ct);

        var occurredAt = new DateTimeOffset(quote.CreatedAt, TimeSpan.Zero);
        var payload = new QuoteCreated(quote.Id, quote.Author, quote.Text, occurredAt);
        var messageId = QuoteEventIds.For(QuoteEventTypes.QuoteCreated, quote.Id, occurredAt);

        _context.OutboxMessages.Add(
            OutboxMessage.For(payload, QuoteEventTypes.QuoteCreated, messageId, occurredAt));
        await _context.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);

        _logger.LogInformation("Created quote {Id}", quote.Id);
        return quote;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        var quote = await _context.Quotes.FindAsync(new object[] { id }, ct);
        if (quote is null) return false;

        _context.Quotes.Remove(quote);

        // No chicken-and-egg here the way AddAsync has one: id already exists,
        // so the outbox row can be built and staged before SaveChanges runs at
        // all, and one call - one transaction - covers both writes.
        var occurredAt = _clock.UtcNow;
        var payload = new QuoteDeleted(id, occurredAt);
        var messageId = QuoteEventIds.For(QuoteEventTypes.QuoteDeleted, id, occurredAt);

        _context.OutboxMessages.Add(
            OutboxMessage.For(payload, QuoteEventTypes.QuoteDeleted, messageId, occurredAt));

        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted quote {Id}", id);
        return true;
    }
}
