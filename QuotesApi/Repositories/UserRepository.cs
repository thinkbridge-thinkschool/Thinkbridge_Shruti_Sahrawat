using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Repositories;

public class UserRepository : IUserRepository
{
    private readonly QuotesDbContext _context;
    private readonly ILogger<UserRepository> _logger;

    public UserRepository(QuotesDbContext context, ILogger<UserRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public Task<User?> FindByEmailAsync(string email, CancellationToken ct)
        => _context.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

    public async Task<User?> GetByIdAsync(int id, CancellationToken ct)
        => await _context.Users.FindAsync(new object[] { id }, ct);

    public async Task<User?> AddAsync(User user, CancellationToken ct)
    {
        _context.Users.Add(user);

        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The unique index on Email rejected it: another request registered
            // this address between our duplicate check and this insert. Caught
            // here rather than left to the exception middleware because it is
            // not a server fault - it is the 409 the endpoint would have
            // returned anyway had the check happened a few milliseconds later.
            //
            // The tracked entity is detached first. Leaving a failed insert in
            // the change tracker means the *next* SaveChanges on this scoped
            // context retries it and throws again, turning one rejected
            // registration into an unrelated request failing.
            _context.Entry(user).State = EntityState.Detached;
            _logger.LogInformation("Registration for an already-registered address was rejected by the unique index");
            return null;
        }

        _logger.LogInformation("Created user {UserId} with role {Role}", user.Id, user.Role);
        return user;
    }
}
