using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quotes.Messaging.Consuming;
using Quotes.Messaging.Contracts;
using Quotes.Messaging.Data;

namespace Quotes.Tests.Unit;

/// <summary>
/// The behaviour these cover is the one that only shows up when something goes
/// wrong in production: Service Bus delivers at least once, so every handler
/// will eventually be asked to process a message it has already processed.
/// </summary>
public sealed class QuoteEventDispatcherTests : IDisposable
{
    private readonly MessagingTestContext _database = new();
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Dispose() => _database.Dispose();

    private QuoteEventDispatcher DispatcherFor(MessagingDbContext db, string instanceId)
        => new(db, NullLogger<QuoteEventDispatcher>.Instance, instanceId, new FixedTimeProvider(Now));

    private static IncomingMessage CreatedMessage(
        string messageId, int quoteId, string author = "Ada Lovelace", int deliveryCount = 1)
        => new(
            messageId,
            QuoteEventTypes.QuoteCreated,
            JsonSerializer.Serialize(
                new QuoteCreated(quoteId, author, "Some text", Now), JsonOptions),
            deliveryCount);

    [Fact]
    public async Task DispatchAsync_OnFirstDelivery_DoesTheWorkAndRecordsIt()
    {
        await using var db = _database.CreateContext();

        var outcome = await DispatcherFor(db, "instance-a")
            .DispatchAsync(CreatedMessage("msg-1", 101), new SearchIndexHandler());

        outcome.Should().Be(DispatchOutcome.Processed);

        await using var verify = _database.CreateContext();
        (await verify.IndexedQuotes.CountAsync()).Should().Be(1);
        (await verify.ProcessedMessages.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_WhenTheSameMessageIsRedelivered_DoesNotDoTheWorkTwice()
    {
        // The central guarantee. A redelivery after a lock expiry looks exactly
        // like this: same message id, same consumer, higher delivery count.
        var handler = new SearchIndexHandler();

        await using (var first = _database.CreateContext())
        {
            await DispatcherFor(first, "instance-a")
                .DispatchAsync(CreatedMessage("msg-1", 101), handler);
        }

        await using (var second = _database.CreateContext())
        {
            var outcome = await DispatcherFor(second, "instance-a")
                .DispatchAsync(CreatedMessage("msg-1", 101, deliveryCount: 2), handler);

            outcome.Should().Be(DispatchOutcome.DuplicateIgnored);
        }

        await using var verify = _database.CreateContext();
        (await verify.IndexedQuotes.CountAsync()).Should().Be(1);
        (await verify.ProcessedMessages.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_WhenTwoInstancesRaceTheSameMessage_ExactlyOneWins()
    {
        // Models the competing-consumer race deterministically rather than by
        // starting two threads and hoping they collide: both contexts are opened
        // before either writes, which is precisely the interleaving a
        // check-then-act implementation gets wrong. Written this way it fails
        // reliably against a broken implementation instead of occasionally.
        await using var contextA = _database.CreateContext();
        await using var contextB = _database.CreateContext();

        var handler = new SearchIndexHandler();
        var message = CreatedMessage("msg-race", 202);

        var first = await DispatcherFor(contextA, "instance-a").DispatchAsync(message, handler);
        var second = await DispatcherFor(contextB, "instance-b").DispatchAsync(message, handler);

        first.Should().Be(DispatchOutcome.Processed);
        second.Should().Be(DispatchOutcome.DuplicateIgnored);

        await using var verify = _database.CreateContext();
        (await verify.ProcessedMessages.CountAsync()).Should().Be(1);
        (await verify.IndexedQuotes.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_WhenTwoDifferentConsumersGetTheSameMessage_BothDoTheirOwnWork()
    {
        // The subtlety that a MessageId-only ledger silently breaks. A topic
        // fans one message out to every subscription, and each subscription is
        // meant to act on it independently. Deduplicating on message id alone
        // would make whichever consumer ran second skip its work with no error
        // anywhere - the audit trail would simply develop holes.
        var message = CreatedMessage("msg-shared", 303);

        await using (var db = _database.CreateContext())
        {
            (await DispatcherFor(db, "instance-a").DispatchAsync(message, new SearchIndexHandler()))
                .Should().Be(DispatchOutcome.Processed);
        }

        await using (var db = _database.CreateContext())
        {
            (await DispatcherFor(db, "instance-a").DispatchAsync(message, new AuditLogHandler()))
                .Should().Be(DispatchOutcome.Processed);
        }

        await using var verify = _database.CreateContext();
        (await verify.IndexedQuotes.CountAsync()).Should().Be(1);
        (await verify.AuditEntries.CountAsync()).Should().Be(1);
        (await verify.ProcessedMessages.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task DispatchAsync_WhenTheHandlerRejectsTheMessage_WritesNothingAtAll()
    {
        // No half-processed state: the ledger must not record a message whose
        // work never happened, or a later valid redelivery would be dismissed
        // as a duplicate and the work would be lost for good.
        await using var db = _database.CreateContext();

        var poison = new IncomingMessage(
            "msg-poison", QuoteEventTypes.QuoteCreated, "{ this is not json", DeliveryCount: 1);

        var act = async () => await DispatcherFor(db, "instance-a")
            .DispatchAsync(poison, new SearchIndexHandler());

        await act.Should().ThrowAsync<PoisonMessageException>();

        await using var verify = _database.CreateContext();
        (await verify.ProcessedMessages.CountAsync()).Should().Be(0);
        (await verify.IndexedQuotes.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_RecordsWhichInstanceProcessedTheMessage()
    {
        // This is what turns "competing consumers work" from a claim about log
        // output into something assertable from the data afterwards.
        await using (var db = _database.CreateContext())
        {
            await DispatcherFor(db, "worker-1").DispatchAsync(CreatedMessage("msg-1", 101), new SearchIndexHandler());
        }

        await using (var db = _database.CreateContext())
        {
            await DispatcherFor(db, "worker-2").DispatchAsync(CreatedMessage("msg-2", 102), new SearchIndexHandler());
        }

        await using var verify = _database.CreateContext();
        var ledger = await verify.ProcessedMessages.AsNoTracking().OrderBy(p => p.MessageId).ToListAsync();

        ledger.Select(p => p.ProcessedBy).Should().BeEquivalentTo(["worker-1", "worker-2"]);
        ledger.Should().OnlyContain(p => p.Consumer == "search-indexer");
    }
}
