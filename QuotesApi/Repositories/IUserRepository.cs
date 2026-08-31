using QuotesApi.Models;

namespace QuotesApi.Repositories;

public interface IUserRepository
{
    /// <param name="email">
    /// Must already be normalised by <see cref="User.NormalizeEmail"/>. The
    /// stored column holds normalised addresses, so an un-normalised lookup
    /// silently misses - and a missed lookup at registration means a duplicate
    /// account, while a missed lookup at login means a correct password is
    /// rejected.
    /// </param>
    Task<User?> FindByEmailAsync(string email, CancellationToken ct);

    Task<User?> GetByIdAsync(int id, CancellationToken ct);

    /// <returns>
    /// The saved user, or null when the unique email index rejected the insert
    /// because another request registered the same address first.
    /// </returns>
    Task<User?> AddAsync(User user, CancellationToken ct);
}
