using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Quotes.Messaging.Consuming;
using Quotes.Messaging.Contracts;
using Quotes.Messaging.Publishing;

namespace Quotes.Tests.Unit;

/// <summary>
/// Classification tests. Whether a failure is poison or transient decides
/// whether the message is dead-lettered immediately or retried until the broker
/// gives up - opposite treatments, and getting it backwards is silently
/// expensive either way.
/// </summary>
public sealed class MessageHandlerTests : IDisposable
{
    private readonly MessagingTestContext _database = new();
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Dispose() => _database.Dispose();

    private static IncomingMessage Message(string body, string eventType = QuoteEventTypes.QuoteCreated)
        => new("msg-1", eventType, body, DeliveryCount: 1);

    private static string CreatedBody(int quoteId = 1, string author = "Ada Lovelace")
        => JsonSerializer.Serialize(new QuoteCreated(quoteId, author, "Some text", Now), JsonOptions);

    [Fact]
    public async Task SearchIndexHandler_WhenTheBodyIsNotJson_IsPoisonAndNamesTheReason()
    {
        await using var db = _database.CreateContext();

        var act = async () => await new SearchIndexHandler()
            .ApplyAsync(Message("{ not json at all"), db, Now, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<PoisonMessageException>();

        // The reason travels to the dead-letter queue as DeadLetterReason, so
        // whoever opens the DLQ can tell what happened without the logs.
        thrown.Which.Reason.Should().Be("MalformedJson");
    }

    [Theory]
    [InlineData(0, "InvalidQuoteId")]
    [InlineData(-1, "InvalidQuoteId")]
    public async Task SearchIndexHandler_WhenTheQuoteIdCannotBeValid_IsPoison(int quoteId, string expectedReason)
    {
        await using var db = _database.CreateContext();

        var act = async () => await new SearchIndexHandler()
            .ApplyAsync(Message(CreatedBody(quoteId)), db, Now, CancellationToken.None);

        (await act.Should().ThrowAsync<PoisonMessageException>()).Which.Reason.Should().Be(expectedReason);
    }

    [Fact]
    public async Task SearchIndexHandler_WhenTheSubscriptionFilterLetsThroughAnEventItCannotHandle_IsPoison()
    {
        await using var db = _database.CreateContext();

        var act = async () => await new SearchIndexHandler()
            .ApplyAsync(Message(CreatedBody(), QuoteEventTypes.QuoteDeleted), db, Now, CancellationToken.None);

        (await act.Should().ThrowAsync<PoisonMessageException>()).Which.Reason.Should().Be("UnexpectedEventType");
    }

    [Fact]
    public async Task SearchIndexHandler_WhenTheFailureCouldBeTransient_IsNotPoisonSoItGetsRetried()
    {
        // The distinction that matters. This must NOT be a PoisonMessageException:
        // a downstream outage should be abandoned and redelivered, and only
        // dead-lettered by the broker once the delivery count says retrying is
        // hopeless. Classifying it as poison would dead-letter real work on its
        // first delivery during a blip that would have cleared in seconds.
        await using var db = _database.CreateContext();

        var act = async () => await new SearchIndexHandler()
            .ApplyAsync(Message(CreatedBody(author: SearchIndexHandler.AlwaysFailsAuthor)), db, Now, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<Exception>();
        thrown.Which.Should().BeOfType<InvalidOperationException>();
        thrown.Which.Should().NotBeOfType<PoisonMessageException>();
    }

    [Fact]
    public async Task SearchIndexHandler_WhenTheQuoteIsAlreadyIndexed_UpdatesInsteadOfFailing()
    {
        // A projection has to be rebuildable by replaying the topic, which means
        // re-indexing a quote it already holds must converge rather than throw.
        await using var db = _database.CreateContext();
        var handler = new SearchIndexHandler();

        await handler.ApplyAsync(Message(CreatedBody(7, "First Author")), db, Now, CancellationToken.None);
        await db.SaveChangesAsync();

        await handler.ApplyAsync(Message(CreatedBody(7, "Corrected Author")), db, Now, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var verify = _database.CreateContext();
        var indexed = await verify.IndexedQuotes.AsNoTracking().SingleAsync();
        indexed.Author.Should().Be("Corrected Author");
    }

    [Fact]
    public async Task AuditLogHandler_RecordsEventTypesTheSearchIndexerNeverSees()
    {
        // audit-log's rule is a catch-all where search-indexer filters on event
        // type, so this is the half of the fan-out that proves the two
        // subscriptions receive genuinely different traffic.
        await using var db = _database.CreateContext();

        var deletedBody = JsonSerializer.Serialize(new QuoteDeleted(42, Now), JsonOptions);

        await new AuditLogHandler()
            .ApplyAsync(Message(deletedBody, QuoteEventTypes.QuoteDeleted), db, Now, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var verify = _database.CreateContext();
        var entry = await verify.AuditEntries.AsNoTracking().SingleAsync();
        entry.EventType.Should().Be(QuoteEventTypes.QuoteDeleted);
        entry.QuoteId.Should().Be(42);
    }

    [Fact]
    public void QuoteEventIds_ForTheSameEvent_ProducesTheSameIdEveryTime()
    {
        // If the id were generated per send attempt, a publisher retry after a
        // lost acknowledgement would look like a brand new event and no
        // consumer-side ledger could ever recognise it as a duplicate. The
        // deduplication story starts here, not at the consumer.
        var occurredAt = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

        var first = QuoteEventIds.For(QuoteEventTypes.QuoteCreated, 101, occurredAt);
        var second = QuoteEventIds.For(QuoteEventTypes.QuoteCreated, 101, occurredAt);

        first.Should().Be(second);
        QuoteEventIds.For(QuoteEventTypes.QuoteCreated, 102, occurredAt).Should().NotBe(first);
    }
}
