using Microsoft.EntityFrameworkCore;
using QuotesApi.Models;
using QuotesApi.Domain; 

namespace QuotesApi.Data;

public class QuotesDbContext : DbContext
{
    public QuotesDbContext(DbContextOptions<QuotesDbContext> options) : base(options) { }

    public DbSet<Quote> Quotes => Set<Quote>();
    
    // 1. Added the Collections DbSet
    public DbSet<Collection> Collections { get; set; } 

    // Accounts. See Models/User.cs.
    public DbSet<User> Users => Set<User>();

    // The outbox. See Models/OutboxMessage.cs for why it exists.
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Your existing Quote configuration
        modelBuilder.Entity<Quote>(entity =>
        {
            entity.HasKey(q => q.Id);
            entity.Property(q => q.Author).IsRequired().HasMaxLength(200);
            entity.Property(q => q.Text).IsRequired().HasMaxLength(1000);

            // Indexed because it is now in the WHERE clause of the busiest
            // query in the API: every listing a non-admin makes filters by it.
            // Without the index that filter is a full table scan on the one
            // path every signed-in user hits on every page load.
            entity.HasIndex(q => q.OwnerId);

            // No foreign key to Users on purpose. A real FK would mean either
            // cascading a user's deletion into their quotes or blocking the
            // deletion outright, and neither is a decision this API has been
            // asked to make yet - there is no delete-account endpoint. Leaving
            // it as a plain indexed column keeps that choice open instead of
            // baking one in through a constraint nobody chose deliberately.
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(o => o.Id);
            entity.Property(o => o.MessageId).IsRequired().HasMaxLength(200);
            entity.Property(o => o.EventType).IsRequired().HasMaxLength(64);
            entity.Property(o => o.Payload).IsRequired();
            entity.Property(o => o.OccurredAt).IsRequired();

            // Unique, not just indexed. This is what makes QuoteRepository's
            // atomicity argument checkable rather than assumed: a duplicate
            // MessageId can only arrive here if the same event tried to write
            // an outbox row twice, and the constraint - not application code -
            // is what refuses the second one and rolls back whatever
            // transaction it was part of.
            entity.HasIndex(o => o.MessageId).IsUnique();

            // Every relay poll is "WHERE SentAt IS NULL", so the column that
            // decides which rows even get scanned is the one that needs the
            // index. A filtered index (WHERE SentAt IS NULL) would be tighter
            // at production scale, but SQLite and SQL Server spell that
            // differently and this app runs on both - a plain index on the
            // column is the one definition that is correct on either.
            entity.HasIndex(o => o.SentAt);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Email).IsRequired().HasMaxLength(User.MaxEmailLength);
            entity.Property(u => u.PasswordHash).IsRequired().HasMaxLength(100);
            entity.Property(u => u.Role).IsRequired().HasMaxLength(20);

            // Unique, so that two accounts cannot claim the same address. The
            // registration endpoint checks for a duplicate first and answers
            // 409, but that check and the insert are two separate statements:
            // two requests arriving together can both pass the check before
            // either inserts. This index is what actually makes it impossible
            // rather than merely unlikely - the second insert fails at the
            // database, which is the only place the race cannot slip through.
            entity.HasIndex(u => u.Email).IsUnique();
        });

        // 2. Added the Collection and CollectionItem configuration
        modelBuilder.Entity<Collection>(builder =>
        {
            builder.HasKey(c => c.Id);
            builder.Property(c => c.Name).IsRequired().HasMaxLength(80);
            builder.Property(c => c.OwnerId).IsRequired();

            // Maps the CollectionItem as an Owned Entity (Value Object)
            builder.OwnsMany(c => c.Items, itemBuilder =>
            {
                itemBuilder.WithOwner().HasForeignKey("CollectionId");
                itemBuilder.Property<int>("Id");
                itemBuilder.HasKey("Id");
                itemBuilder.Property(i => i.QuoteId).IsRequired();
                itemBuilder.Property(i => i.AddedAt).IsRequired();
            });
        });
    }
}