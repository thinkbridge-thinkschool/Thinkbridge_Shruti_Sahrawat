using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Quotes.Messaging.Data;

namespace Quotes.Tests.Unit;

/// <summary>
/// A throwaway SQLite database for one test, backed by a real file.
/// </summary>
/// <remarks>
/// <para>Deliberately SQLite and not EF Core's InMemory provider. InMemory does
/// not enforce unique or primary key constraints at all - it is a dictionary
/// wearing a DbContext. Every idempotency test here passes trivially against
/// it while proving nothing, because the exact mechanism under test is a
/// database constraint rejecting a second insert. A test that cannot fail when
/// the behaviour is absent is worse than no test: it reports safety that was
/// never checked.</para>
///
/// <para>A file rather than <c>:memory:</c> because several tests open more
/// than one connection to the same database to model two competing consumers,
/// and an in-memory SQLite database belongs to its single connection.</para>
/// </remarks>
public sealed class MessagingTestContext : IDisposable
{
    private readonly string _path;

    public MessagingTestContext()
    {
        _path = Path.Combine(Path.GetTempPath(), $"quotes-msg-test-{Guid.NewGuid():N}.db");

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public MessagingDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseSqlite($"Data Source={_path};Default Timeout=30")
            .Options;

        return new MessagingDbContext(options);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_path)) File.Delete(_path); } catch (IOException) { /* best effort */ }
    }
}
