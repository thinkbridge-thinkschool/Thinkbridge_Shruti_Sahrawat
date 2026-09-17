using Capstone.Curation.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Capstone.Api.Tests;

/// <summary>
/// The schema, rather than the behaviour: that the committed migrations build
/// the database this application expects to find.
/// </summary>
/// <remarks>
/// The only file in this project that touches the host's container instead of
/// its HTTP surface, because a schema has no endpoint to ask.
///
/// This is the layer where two of this capstone's three worst days actually
/// lived. Day 24's Finding 17 was a migration generated against one provider
/// and applied to another. Day 30's first red test run was EF refusing to
/// translate <c>ORDER BY</c> over a <c>DateTimeOffset</c> on SQLite - a defect
/// that compiles, passes review, and only exists at the boundary between the
/// model and the store. Neither was findable in a unit test and neither was
/// findable in an endpoint test that had already had its schema created for it
/// by <c>EnsureCreated</c>.
/// </remarks>
public sealed class MigrationTests
{
    [Fact]
    public async Task The_committed_migrations_apply_to_an_empty_database_and_leave_nothing_pending()
    {
        using var app = new CapstoneApiFactory();
        using var client = app.CreateClient();
        using var scope = app.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<CurationDbContext>();

        var applied = await db.Database.GetAppliedMigrationsAsync();

        applied.Should().Contain(
            name => name.EndsWith("_InitialCreate", StringComparison.Ordinal));
        applied.Should().Contain(
            name => name.EndsWith("_AddOutboxMessages", StringComparison.Ordinal));

        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_model_carries_no_change_the_migrations_have_not_been_told_about()
    {
        using var app = new CapstoneApiFactory();
        using var client = app.CreateClient();
        using var scope = app.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<CurationDbContext>();

        // The failure this catches is the quiet one: somebody edits
        // OnModelCreating, the tests still pass because every test database is
        // built from the model, and the change reaches production as a column
        // that was never added. EF can answer it directly - it diffs the model
        // against the snapshot the last migration wrote.
        db.Database.HasPendingModelChanges().Should().BeFalse(
            "run `dotnet ef migrations add <name>` - the model has moved and the migrations have not");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_relays_own_query_runs_against_the_schema_the_migrations_built()
    {
        using var app = new CapstoneApiFactory();
        using var client = app.CreateClient();
        using var scope = app.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<CurationDbContext>();

        // Not an arbitrary query. This is the exact shape EfOutboxStore issues
        // on every poll - filter on SentAt, order by OccurredAt, take a bounded
        // batch - and on 16 September it threw NotSupportedException here,
        // because SQLite's provider will not translate ORDER BY over a
        // DateTimeOffset. The column is stored as UTC ticks now. This test is
        // what would notice if a future migration quietly changed it back.
        var read = async () => await db.OutboxMessages
            .Where(message => message.SentAt == null)
            .OrderBy(message => message.OccurredAt)
            .Take(20)
            .ToListAsync();

        await read.Should().NotThrowAsync(
            "the relay's only query has to be translatable on the provider this schema was built for");

        var collections = async () => await db.Collections.Include(c => c.Items).CountAsync();

        await collections.Should().NotThrowAsync("the owned Items table has to exist too");
    }
}
