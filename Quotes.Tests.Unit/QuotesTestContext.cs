using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Quotes.Outbox;
using QuotesApi.Data;

namespace Quotes.Tests.Unit;

/// <summary>
/// A throwaway SQLite database for one test, backed by a real file.
/// </summary>
/// <remarks>
/// The same shape as <see cref="MessagingTestContext"/>, for the same reason:
/// EF Core's InMemory provider does not enforce unique constraints, and the
/// exact thing Day 20's atomicity tests need to observe is a unique
/// constraint on OutboxMessages.MessageId rejecting a second insert and
/// rolling back the transaction it was part of. InMemory would let that
/// insert through and every one of those tests would pass while proving
/// nothing.
/// </remarks>
public sealed class QuotesTestContext : IDisposable
{
    private readonly string _path;

    public QuotesTestContext()
    {
        _path = Path.Combine(Path.GetTempPath(), $"quotes-api-test-{Guid.NewGuid():N}.db");

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public QuotesDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<QuotesDbContext>()
            .UseSqlite($"Data Source={_path};Default Timeout=30")
            .Options;

        return new QuotesDbContext(options);
    }

    /// <summary>
    /// An <see cref="OutboxDbContext"/> pointed at the same physical file as
    /// <see cref="CreateContext"/> - the relay's own view of the table
    /// QuotesApi just wrote to, exactly as it is in production: two different
    /// DbContext types, one table, one file.
    /// </summary>
    public OutboxDbContext CreateOutboxContext()
    {
        var options = new DbContextOptionsBuilder<OutboxDbContext>()
            .UseSqlite($"Data Source={_path};Default Timeout=30")
            .Options;

        return new OutboxDbContext(options);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_path)) File.Delete(_path); } catch (IOException) { /* best effort */ }
    }
}
