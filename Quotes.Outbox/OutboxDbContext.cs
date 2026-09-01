using Microsoft.EntityFrameworkCore;

namespace Quotes.Outbox;

/// <summary>
/// A read/write view of exactly one table QuotesApi already owns.
/// </summary>
/// <remarks>
/// This context never creates or migrates schema - see Program.cs's startup
/// probe. QuotesApi's own migrations (QuotesApi/Migrations and
/// Quotes.Tests.Integration/Migrations/SqlServer) are the only place the
/// OutboxMessages table is created or changed; a relay that could also
/// migrate the database it does not own would be a second, competing source
/// of truth for a schema, which is exactly the kind of silent drift Day 19's
/// SQLite-versus-SQL-Server migration split was written to avoid.
///
/// Column names are matched by convention - <see cref="OutboxRecord"/>'s
/// property names are identical to <c>QuotesApi.Models.OutboxMessage</c>'s -
/// rather than restated with explicit HasColumnName calls, so the two stay in
/// sync by construction: renaming a column on one side without the other
/// breaks the build here on the very next query, not silently at run time.
/// </remarks>
public sealed class OutboxDbContext : DbContext
{
    public OutboxDbContext(DbContextOptions<OutboxDbContext> options) : base(options) { }

    public DbSet<OutboxRecord> Outbox => Set<OutboxRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxRecord>(entity =>
        {
            entity.ToTable("OutboxMessages");
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Id).ValueGeneratedOnAdd();
        });
    }
}
