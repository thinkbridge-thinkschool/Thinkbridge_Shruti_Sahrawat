using Capstone.Curation.Domain;
using Microsoft.EntityFrameworkCore;

namespace Capstone.Curation.Infrastructure;

/// <summary>
/// EF Core persistence for the Curation module. One aggregate, one DbSet -
/// reads that feed screens do not come through here (see ICollectionRepository).
/// </summary>
/// <remarks>
/// Day 1 of the build plan in Days/day-28/README.md. Collection is a mapped
/// entity, Items an owned collection, and CollectionId/CuratorId/QuoteId all
/// pass through value converters so the domain keeps its own types and the
/// database sees primitives - the domain project itself never references EF
/// Core, which is what DeclaredReferenceTests checks on every build.
/// </remarks>
public sealed class CurationDbContext(DbContextOptions<CurationDbContext> options)
    : DbContext(options)
{
    public DbSet<Collection> Collections => Set<Collection>();

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
    }
}
