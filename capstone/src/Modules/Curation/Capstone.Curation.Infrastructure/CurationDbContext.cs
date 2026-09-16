using Capstone.Curation.Domain;
using Capstone.Curation.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Capstone.Curation.Infrastructure;

/// <summary>
/// EF Core persistence for the Curation module: the aggregate, and the outbox
/// that commits with it.
/// </summary>
/// <remarks>
/// Day 1 of the build plan mapped Collection. Day 2 added OutboxMessages to
/// the same context, and the fact that it is the *same* context is the design
/// rather than a shortcut - one DbContext means one transaction, and one
/// transaction is the only thing that makes "the state change and the
/// announcement commit together" true instead of aspirational.
///
/// Collection is a mapped entity, Items an owned collection, and
/// CollectionId/CuratorId/QuoteId all pass through value converters so the
/// domain keeps its own types and the database sees primitives - the domain
/// project itself never references EF Core, which is what
/// DeclaredReferenceTests checks on every build.
/// </remarks>
public sealed class CurationDbContext(DbContextOptions<CurationDbContext> options)
    : DbContext(options)
{
    public DbSet<Collection> Collections => Set<Collection>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Collection>(collection =>
        {
            collection.ToTable("Collections");

            collection.HasKey(c => c.Id);

            // The domain mints a Guid v7 before this entity is ever tracked -
            // see CollectionId's own remarks. ValueGeneratedNever is what stops
            // EF's default "Guid keys are server-generated" convention from
            // silently overwriting that id with a fresh one on insert, which
            // would quietly break the one-transaction property the domain
            // comment says this id exists to buy.
            collection.Property(c => c.Id)
                .HasConversion(id => id.Value, value => new CollectionId(value))
                .ValueGeneratedNever();

            collection.Property(c => c.CuratorId)
                .HasConversion(id => id.Value, value => new CuratorId(value))
                .HasMaxLength(200)
                .IsRequired();

            collection.Property(c => c.Name)
                .HasMaxLength(Collection.MaxNameLength)
                .IsRequired();

            // Stored as text rather than the enum's underlying int so a row is
            // readable, and a mistaken value obvious, without the mapping in
            // front of you.
            collection.Property(c => c.Status)
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();

            collection.Property(c => c.PublishedAt);

            // Items is exposed publicly only as IReadOnlyList<CollectionItem>,
            // which EF cannot populate directly - it has no Add. Pointing the
            // navigation at the private _items field instead lets EF
            // materialise into the same list the aggregate's own AddItem/
            // RemoveItem mutate, without the aggregate exposing a public
            // setter that would let anyone bypass those invariants.
            collection.Metadata.FindNavigation(nameof(Collection.Items))!
                .SetPropertyAccessMode(PropertyAccessMode.Field);

            collection.OwnsMany(c => c.Items, item =>
            {
                item.ToTable("CollectionItems");

                item.WithOwner().HasForeignKey("CollectionId");

                // CollectionItem has no identity of its own in the domain -
                // see its own remarks - so the owned table gets a synthetic
                // key rather than the aggregate inventing one it does not
                // need. Duplicates are already prevented by Collection.AddItem,
                // not by a unique constraint here.
                item.Property<int>("Id");
                item.HasKey("Id");

                item.Property(i => i.QuoteId)
                    .HasConversion(id => id.Value, value => new QuoteId(value))
                    .IsRequired();

                item.Property(i => i.AddedAt).IsRequired();
            });
        });

        // Day 2: the outbox.
        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("OutboxMessages");

            // MessageId is both the key and the idempotency key - see
            // OutboxMessage. The unique constraint that comes free with a
            // primary key is doing real work: staging the same MessageId twice
            // fails at the database rather than becoming a message a
            // subscriber would deduplicate away as a redelivery.
            outbox.HasKey(message => message.MessageId);

            outbox.Property(message => message.MessageId).ValueGeneratedNever();

            outbox.Property(message => message.EventType)
                .HasMaxLength(200)
                .IsRequired();

            // No length cap: the payload is a serialised integration event and
            // capping it would turn "somebody added a field to a contract"
            // into a truncation at insert time.
            outbox.Property(message => message.Payload).IsRequired();

            // Stored as UTC ticks rather than as a DateTimeOffset, and this is
            // a portability fix rather than a preference. SQLite cannot ORDER
            // BY a DateTimeOffset at all - EF's provider refuses to translate
            // it, with a NotSupportedException telling you to convert to a
            // supported type - and ordering this column is the relay's entire
            // query. A long orders identically on both providers.
            //
            // UtcTicks, not Ticks: Ticks is the local wall-clock reading and
            // would sort two instants written in different offsets into the
            // wrong order, which is exactly the bug a queue ordered by time
            // cannot afford. Reading back at TimeSpan.Zero is lossless for
            // ordering and for every comparison anything here makes, and
            // loses only the original offset, which nothing reads.
            outbox.Property(message => message.OccurredAt)
                .HasConversion(
                    occurredAt => occurredAt.UtcTicks,
                    ticks => new DateTimeOffset(ticks, TimeSpan.Zero))
                .IsRequired();

            // Not converted, deliberately. SentAt is only ever tested for null
            // - "has a subscriber taken this yet" - and never ordered or
            // compared, so it does not hit the restriction above and there is
            // no reason to make its column harder to read than it needs to be.
            outbox.Property(message => message.SentAt);

            // The relay issues exactly one query - unsent, oldest first - and
            // this is it. A plain composite index rather than a filtered one
            // (WHERE SentAt IS NULL) because the filter syntax is
            // provider-specific and this template targets two providers; the
            // filtered version is strictly better on a table where most rows
            // are eventually sent, and is worth revisiting when the sent rows
            // start outnumbering the unsent ones by orders of magnitude.
            //
            // Ordering by a timestamp is the weaker half of this design and is
            // worth saying so: two events staged in the same commit can share
            // an OccurredAt, and their relative order is then undefined. The
            // stronger answer is a monotonic sequence column, which is what a
            // production outbox orders by. It is not here because SQLite only
            // auto-increments an INTEGER PRIMARY KEY, so a sequence would mean
            // making MessageId a unique index instead of the key - a
            // defensible redesign, and a bigger one than this day needs. The
            // relay promises subscribers nothing about order either way.
            outbox.HasIndex(message => new { message.SentAt, message.OccurredAt });
        });
    }
}
