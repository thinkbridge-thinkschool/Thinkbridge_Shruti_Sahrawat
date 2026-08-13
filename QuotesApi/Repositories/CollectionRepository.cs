using Microsoft.EntityFrameworkCore;
using QuotesApi.Domain;
using QuotesApi.Data;

namespace QuotesApi.Repositories;

public interface ICollectionRepository
{
    Task<Collection?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Collection>> GetAllAsync(CancellationToken cancellationToken = default);
    Task AddAsync(Collection collection, CancellationToken cancellationToken = default);
    Task UpdateAsync(Collection collection, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}

public class CollectionRepository : ICollectionRepository
{
    private readonly QuotesDbContext _context;

    public CollectionRepository(QuotesDbContext context) => _context = context;

    public async Task<Collection?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.Collections
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    // Single query: Include eager-loads Items in one round trip (was N+1).
    public async Task<IReadOnlyList<Collection>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Collections
            .AsNoTracking()
            .Include(c => c.Items)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Collection collection, CancellationToken cancellationToken = default)
    {
        await _context.Collections.AddAsync(collection, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Collection collection, CancellationToken cancellationToken = default)
    {
        _context.Collections.Update(collection);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var collection = await GetByIdAsync(id, cancellationToken);
        if (collection != null)
        {
            _context.Collections.Remove(collection);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}