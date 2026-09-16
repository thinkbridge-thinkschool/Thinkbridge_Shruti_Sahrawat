using System.Text.Json;
using Capstone.Api;
using Capstone.Curation.Contracts;
using Capstone.Curation.Domain;
using Capstone.Curation.Infrastructure.Outbox;
using Capstone.Sharing.Application;
using Capstone.Sharing.Infrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Capstone.Curation.Infrastructure.Tests;

/// <summary>
/// The day-2 contract: nothing is deleted, delivery is acknowledged only after
/// a subscriber accepts it, and a failure is a retry rather than a loss.
/// </summary>
/// <remarks>
/// These tests exist because the Day 28 design review found a lost message had
/// no record anywhere, and because the fix is the kind that looks correct by
/// inspection and is only actually correct if something exercises the failure
/// path. The scaffold's destructive drain also looked correct by inspection.
/// </remarks>
public sealed class OutboxDrainTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly CurationDbContext _db;
    private readonly EfOutboxStore _outbox;
    private readonly ExplodingFeedWriter _feed = new();
    private readonly InMemoryFollowerDirectory _followers = new();
    private readonly CollectionPublishedHandler _sharingHandler;
    private readonly InProcessRelay _relay;

    public OutboxDrainTests()
    {
        // SQLite in memory, and deliberately not EF's in-memory provider. That
        // provider is not a relational database - no transactions, no
        // constraints, no SQL - so a test passing against it would say nothing
        // about whether one SaveChanges really commits two tables together,
        // which is the single property this day is about. The connection is
        // held open because an in-memory SQLite database exists only as long
        // as a connection to it does.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _db = NewContext();
        _db.Database.EnsureCreated();

        _outbox = new EfOutboxStore(_db, TimeProvider.System);

        _sharingHandler = new CollectionPublishedHandler(
            _followers, _feed, new InMemoryProcessedMessageLog());

        _relay = new InProcessRelay(_outbox, _sharingHandler);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Publishing_commits_the_collection_and_its_outbox_row_together()
    {
        var collection = await PublishACollectionAsync();

        // Read through a second context over the same connection, so these are
        // reads of committed rows rather than of this test's change tracker.
        await using var reader = NewContext();

        var saved = await reader.Collections
            .FirstOrDefaultAsync(candidate => candidate.Id == collection.Id);

        saved.Should().NotBeNull("the aggregate is committed");

        var staged = await reader.OutboxMessages.ToListAsync();

        staged.Should().HaveCount(1, "publishing raises exactly one integration event");
        staged[0].SentAt.Should().BeNull("nothing has delivered it yet");
    }

    [Fact]
    public async Task A_subscriber_that_throws_leaves_the_row_unsent()
    {
        await PublishACollectionAsync();
        _followers.Follow("alice", "bob");
        _feed.Explode = true;

        Func<Task> drain = () => _relay.DrainAsync(CancellationToken.None);

        await drain.Should().ThrowAsync<InvalidOperationException>(
            "the relay does not swallow a subscriber failure - RelayHostedService logs it and polls again");

        var unsent = await _outbox.ReadUnsentAsync(10, CancellationToken.None);

        unsent.Should().HaveCount(1,
            "the row has to survive a failed delivery; the scaffold's destructive drain is what made "
            + "this the most expensive flaw in the design review");
    }

    [Fact]
    public async Task The_next_drain_delivers_the_row_once_the_subscriber_recovers()
    {
        await PublishACollectionAsync();
        _followers.Follow("alice", "bob");

        _feed.Explode = true;

        Func<Task> firstAttempt = () => _relay.DrainAsync(CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<InvalidOperationException>();

        // The feed store comes back.
        _feed.Explode = false;

        var delivered = await _relay.DrainAsync(CancellationToken.None);

        delivered.Should().Be(1, "the retry finds the row still unsent and delivers it");
        _feed.Written.Should().ContainSingle().Which.Follower.Should().Be("bob");

        (await _outbox.ReadUnsentAsync(10, CancellationToken.None))
            .Should().BeEmpty("and only now is it acknowledged");
    }

    [Fact]
    public async Task A_delivered_row_is_not_delivered_again()
    {
        await PublishACollectionAsync();
        _followers.Follow("alice", "bob");

        (await _relay.DrainAsync(CancellationToken.None)).Should().Be(1);

        (await _relay.DrainAsync(CancellationToken.None)).Should().Be(0,
            "SentAt is what takes a row out of the relay's query; nothing deletes it");

        _feed.Written.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_redelivered_message_fans_out_once()
    {
        // The case the acknowledge step openly admits to: a subscriber accepts
        // a message and the MarkSent that should have followed does not happen
        // - the relay crashed, the connection dropped - so the next poll
        // delivers it a second time. At-least-once behaving as advertised, and
        // the reason IProcessedMessageLog is load-bearing rather than
        // decoration.
        await PublishACollectionAsync();
        _followers.Follow("alice", "bob");

        var record = (await _outbox.ReadUnsentAsync(1, CancellationToken.None)).Single();
        var message = JsonSerializer.Deserialize<CollectionPublishedIntegrationEvent>(record.Payload);

        message.Should().NotBeNull();

        await _sharingHandler.HandleAsync(message!, CancellationToken.None);
        await _sharingHandler.HandleAsync(message!, CancellationToken.None);

        _feed.Written.Should().HaveCount(1,
            "the second delivery is recognised as the same message rather than a second publish");
    }

    private CurationDbContext NewContext() => new(
        new DbContextOptionsBuilder<CurationDbContext>()
            .UseSqlite(_connection)
            .Options);

    private async Task<Collection> PublishACollectionAsync()
    {
        var collection = Collection.Start(new CuratorId("alice"), "Distributed Systems Wisdom");
        collection.AddItem(new QuoteId(1), Now);
        collection.Publish(Now);

        new CollectionRepository(_db).Add(collection);

        var staged = await new UnitOfWork(_db, _outbox).CommitAsync(CancellationToken.None);

        staged.Should().Be(1);

        return collection;
    }

    /// <summary>
    /// A feed store that is down, rather than a mock configured to throw.
    /// </summary>
    /// <remarks>
    /// The distinction matters for what these tests prove. "The feed store is
    /// unavailable" is the failure CollectionPublishedHandler's own remarks
    /// name as the reason not to fan out inside the publish transaction, so it
    /// is the failure the retry path has to survive.
    /// </remarks>
    private sealed class ExplodingFeedWriter : IFeedWriter
    {
        public bool Explode { get; set; }

        public List<(string Follower, FeedEntry Entry)> Written { get; } = [];

        public Task AppendAsync(string followerId, FeedEntry entry, CancellationToken cancellationToken)
        {
            if (Explode)
            {
                throw new InvalidOperationException("The feed store is unavailable.");
            }

            Written.Add((followerId, entry));

            return Task.CompletedTask;
        }
    }
}
