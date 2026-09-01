using Microsoft.EntityFrameworkCore;

namespace Quotes.Messaging.Data;

/// <summary>
/// The consumer side's own database: the idempotency ledger plus the
/// projections the handlers build.
/// </summary>
/// <remarks>
/// Separate from <c>QuotesDbContext</c> on purpose. The worker is a different
/// service with a different lifecycle; giving it its own context keeps the
/// consumer from taking a dependency on the API's schema, and keeps the
/// idempotency ledger next to the data it protects.
///
/// That adjacency is not cosmetic. The whole correctness argument below rests
/// on the ledger row and the projection row being written in one transaction,
/// which is only possible while they live in the same database.
/// </remarks>
public sealed class MessagingDbContext : DbContext
{
    public MessagingDbContext(DbContextOptions<MessagingDbContext> options) : base(options) { }

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<IndexedQuote> IndexedQuotes => Set<IndexedQuote>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedMessage>(e =>
        {
            // The composite key IS the idempotency mechanism. It is a database
            // constraint, not an application check, and that distinction is the
            // whole design: two worker instances racing on the same message
            // both attempt the insert, and the database - the one place that
            // can actually serialise them - rejects exactly one.
            e.HasKey(p => new { p.MessageId, p.Consumer });
            e.Property(p => p.MessageId).HasMaxLength(128);
            e.Property(p => p.Consumer).HasMaxLength(64);
            e.Property(p => p.ProcessedBy).HasMaxLength(64);
        });

        modelBuilder.Entity<IndexedQuote>(e =>
        {
            e.HasKey(q => q.QuoteId);
            e.Property(q => q.Author).HasMaxLength(200);
            e.Property(q => q.Text).HasMaxLength(1000);
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.EventType).HasMaxLength(64);
            e.Property(a => a.Detail).HasMaxLength(1000);
        });
    }
}
