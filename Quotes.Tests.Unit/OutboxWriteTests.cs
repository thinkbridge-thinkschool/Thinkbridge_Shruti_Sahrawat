using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quotes.Messaging.Contracts;
using Quotes.Messaging.Publishing;
using QuotesApi.Models;
using QuotesApi.Repositories;

namespace Quotes.Tests.Unit;

/// <summary>
/// The write side of Day 20's outbox: does <see cref="QuoteRepository"/>
/// actually put the domain change and the announcement of it in one
/// transaction, and does that transaction really behave atomically when one
/// half of it fails.
/// </summary>
public sealed class OutboxWriteTests : IDisposable
{
    private readonly QuotesTestContext _database = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Dispose() => _database.Dispose();

    private QuoteRepository RepositoryFor(QuotesApi.Data.QuotesDbContext db, TestClock clock)
        => new(db, NullLogger<QuoteRepository>.Instance, clock);

    [Fact]
    public async Task AddAsync_WritesTheQuoteAndAMatchingOutboxRowTogether()
    {
        var clock = new TestClock();

        await using (var db = _database.CreateContext())
        {
            var created = await RepositoryFor(db, clock)
                .AddAsync(Quote.Create("Ada Lovelace", "A valid quote.", clock), CancellationToken.None);

            created.Id.Should().BeGreaterThan(0);
        }

        await using var verify = _database.CreateContext();
        (await verify.Quotes.CountAsync()).Should().Be(1);

        var outbox = await verify.OutboxMessages.SingleAsync();
        outbox.EventType.Should().Be(QuoteEventTypes.QuoteCreated);
        outbox.SentAt.Should().BeNull("the relay has not run yet - see Quotes.Outbox");

        var quoteId = (await verify.Quotes.SingleAsync()).Id;
        outbox.MessageId.Should().Be(QuoteEventIds.For(QuoteEventTypes.QuoteCreated, quoteId, clock.UtcNow));

        var payload = JsonSerializer.Deserialize<QuoteCreated>(outbox.Payload, JsonOptions);
        payload.Should().NotBeNull();
        payload!.QuoteId.Should().Be(quoteId);
        payload.Author.Should().Be("Ada Lovelace");
        payload.Text.Should().Be("A valid quote.");
    }

    [Fact]
    public async Task DeleteAsync_WritesAQuoteDeletedOutboxRow()
    {
        var clock = new TestClock();
        int quoteId;

        await using (var db = _database.CreateContext())
        {
            var created = await RepositoryFor(db, clock)
                .AddAsync(Quote.Create("Grace Hopper", "A valid quote.", clock), CancellationToken.None);
            quoteId = created.Id;
        }

        clock.Advance(TimeSpan.FromMinutes(1));

        await using (var db = _database.CreateContext())
        {
            var deleted = await RepositoryFor(db, clock).DeleteAsync(quoteId, CancellationToken.None);
            deleted.Should().BeTrue();
        }

        await using var verify = _database.CreateContext();
        (await verify.Quotes.CountAsync()).Should().Be(0);

        var deleteOutbox = await verify.OutboxMessages
            .Where(o => o.EventType == QuoteEventTypes.QuoteDeleted)
            .SingleAsync();

        deleteOutbox.MessageId.Should().Be(QuoteEventIds.For(QuoteEventTypes.QuoteDeleted, quoteId, clock.UtcNow));

        var payload = JsonSerializer.Deserialize<QuoteDeleted>(deleteOutbox.Payload, JsonOptions);
        payload.Should().NotBeNull();
        payload!.QuoteId.Should().Be(quoteId);
    }

    /// <summary>
    /// The proof that "one EF transaction" is not just a comment.
    /// </summary>
    /// <remarks>
    /// AddAsync stages the quote insert and the outbox insert as two
    /// SaveChanges calls inside one explicit transaction - see the remarks on
    /// QuoteRepository.AddAsync for why it cannot be one call. Two calls is
    /// only as atomic as the transaction wrapping them actually is, so this
    /// forces the second call to fail - by pre-seeding a row that collides on
    /// the unique MessageId index the insert is about to try - and checks,
    /// from a separate connection, that the quote never committed either.
    /// </remarks>
    [Fact]
    public async Task AddAsync_WhenTheOutboxInsertFails_RollsBackTheQuoteToo()
    {
        var clock = new TestClock();

        // A fresh SQLite table's first autoincrement row is Id 1, which is
        // exactly the id this test's AddAsync call is about to receive - so
        // the message id it will try to insert is predictable ahead of time.
        var collidingMessageId = QuoteEventIds.For(QuoteEventTypes.QuoteCreated, 1, clock.UtcNow);

        await using (var seed = _database.CreateContext())
        {
            seed.OutboxMessages.Add(OutboxMessage.For(
                new { Note = "occupies the message id AddAsync is about to try to use" },
                "SomeUnrelatedEventType",
                collidingMessageId,
                clock.UtcNow));
            await seed.SaveChangesAsync();
        }

        await using var db = _database.CreateContext();
        var repository = RepositoryFor(db, clock);
        var quote = Quote.Create("Ada Lovelace", "A valid quote.", clock);

        var act = () => repository.AddAsync(quote, CancellationToken.None);
        await act.Should().ThrowAsync<DbUpdateException>();

        // A separate connection, not the one whose SaveChanges just failed -
        // this asks what actually committed to the database, not what a
        // failed context still happens to be holding in memory.
        await using var verify = _database.CreateContext();
        (await verify.Quotes.CountAsync()).Should().Be(
            0, "the transaction that failed to write the outbox row must not have left the quote behind either");
        (await verify.OutboxMessages.CountAsync()).Should().Be(
            1, "only the pre-seeded row should exist - the failed attempt's own outbox row must not have partially landed");
    }
}
