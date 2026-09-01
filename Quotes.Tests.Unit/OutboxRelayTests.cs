using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quotes.Messaging.Contracts;
using Quotes.Messaging.Publishing;
using Quotes.Outbox;
using QuotesApi.Models;

namespace Quotes.Tests.Unit;

/// <summary>
/// The read side of Day 20's outbox: does the relay actually publish unsent
/// rows and mark them sent, and - the exercise's central question - what
/// happens to a row when the publish step itself crashes.
/// </summary>
public sealed class OutboxRelayTests : IDisposable
{
    private readonly QuotesTestContext _database = new();
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    public void Dispose() => _database.Dispose();

    private async Task<string> SeedUnsentQuoteCreatedAsync(int quoteId, string author = "Ada Lovelace")
    {
        var messageId = QuoteEventIds.For(QuoteEventTypes.QuoteCreated, quoteId, Now);

        await using var db = _database.CreateContext();
        db.OutboxMessages.Add(OutboxMessage.For(
            new QuoteCreated(quoteId, author, "A valid quote.", Now),
            QuoteEventTypes.QuoteCreated,
            messageId,
            Now));
        await db.SaveChangesAsync();

        return messageId;
    }

    [Fact]
    public async Task RelayBatchAsync_PublishesEveryUnsentRowAndMarksItSent()
    {
        var messageId1 = await SeedUnsentQuoteCreatedAsync(1, "Ada Lovelace");
        var messageId2 = await SeedUnsentQuoteCreatedAsync(2, "Grace Hopper");

        var publisher = new FakeQuoteEventPublisher();

        await using (var outboxDb = _database.CreateOutboxContext())
        {
            var relay = new OutboxRelay(outboxDb, publisher, NullLogger<OutboxRelay>.Instance);
            var results = await relay.RelayBatchAsync(batchSize: 10, CancellationToken.None);

            results.Should().HaveCount(2);
            results.Should().OnlyContain(r => r.Outcome == OutboxRelayOutcome.Published);
        }

        publisher.Sent.Select(s => s.MessageId).Should().BeEquivalentTo([messageId1, messageId2]);

        await using var verify = _database.CreateOutboxContext();
        (await verify.Outbox.CountAsync(o => o.SentAt == null)).Should().Be(0);
    }

    /// <summary>
    /// This is the exercise's crash scenario. A publish that reaches the
    /// broker and then the process dying before the row is marked sent must
    /// not be able to lose the message - the row has to survive, unsent, for
    /// the next poll to try again.
    /// </summary>
    /// <remarks>
    /// The retry that follows republishes with the exact same message id, so
    /// it is a duplicate at the transport level, not a lost message and not a
    /// new one. That duplicate is safe only because Day 19's consumers dedupe
    /// on (MessageId, Consumer) rather than trusting that Service Bus - or
    /// this relay - never sends the same event twice. This test proves the
    /// relay's half: no crash between "sent" and "marked sent" can make a row
    /// disappear. Day 19's <c>QuoteEventDispatcherTests</c> already proves the
    /// consumer's half: a repeated message id does no work twice.
    /// </remarks>
    [Fact]
    public async Task RelayBatchAsync_WhenTheProcessDiesAfterPublishingButBeforeMarkingSent_TheRowSurvivesForRetry()
    {
        var messageId = await SeedUnsentQuoteCreatedAsync(101);

        // Models the crash precisely: the message really did reach the
        // broker (FakeQuoteEventPublisher records it before throwing) and
        // *then* something failed - the process dying is indistinguishable,
        // from the row's perspective, from any other exception thrown at
        // that point.
        var crashingPublisher = new FakeQuoteEventPublisher
        {
            ThrowAfterRecording = new InvalidOperationException(
                "Simulated crash: the broker already has this message."),
        };

        await using (var outboxDb = _database.CreateOutboxContext())
        {
            var relay = new OutboxRelay(outboxDb, crashingPublisher, NullLogger<OutboxRelay>.Instance);
            var results = await relay.RelayBatchAsync(batchSize: 10, CancellationToken.None);

            results.Should().ContainSingle();
            results[0].Outcome.Should().Be(OutboxRelayOutcome.Failed);
        }

        // The broker did receive it - this is not a message that failed to
        // send, it is a message that sent and then the bookkeeping about it
        // failed.
        crashingPublisher.Sent.Should().ContainSingle(s => s.MessageId == messageId);

        // Not lost: the row is exactly where the next poll will find it again.
        await using (var verify = _database.CreateOutboxContext())
        {
            var row = await verify.Outbox.SingleAsync();
            row.SentAt.Should().BeNull();
        }

        // The retry, modelling the relay restarting (or the next poll tick)
        // against a publisher that now succeeds.
        var recoveredPublisher = new FakeQuoteEventPublisher();

        await using (var outboxDb = _database.CreateOutboxContext())
        {
            var relay = new OutboxRelay(outboxDb, recoveredPublisher, NullLogger<OutboxRelay>.Instance);
            var results = await relay.RelayBatchAsync(batchSize: 10, CancellationToken.None);

            results.Should().ContainSingle();
            results[0].Outcome.Should().Be(OutboxRelayOutcome.Published);
        }

        // Same message id both times - the broker saw one event published
        // twice, never two different events, which is exactly what makes the
        // duplicate harmless downstream.
        recoveredPublisher.Sent.Should().ContainSingle(s => s.MessageId == messageId);

        await using var verifyAgain = _database.CreateOutboxContext();
        (await verifyAgain.Outbox.SingleAsync()).SentAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RelayBatchAsync_WhenOneRowFailsToPublish_StillPublishesTheOthersInTheBatch()
    {
        var failingMessageId = await SeedUnsentQuoteCreatedAsync(1, "Ada Lovelace");
        var okMessageId = await SeedUnsentQuoteCreatedAsync(2, "Grace Hopper");

        var publisher = new SelectivelyFailingPublisher(failOnMessageId: failingMessageId);

        await using var outboxDb = _database.CreateOutboxContext();
        var relay = new OutboxRelay(outboxDb, publisher, NullLogger<OutboxRelay>.Instance);
        var results = await relay.RelayBatchAsync(batchSize: 10, CancellationToken.None);

        results.Should().HaveCount(2);
        results.Single(r => r.MessageId == failingMessageId).Outcome.Should().Be(OutboxRelayOutcome.Failed);
        results.Single(r => r.MessageId == okMessageId).Outcome.Should().Be(OutboxRelayOutcome.Published);

        var rows = await outboxDb.Outbox.ToListAsync();
        rows.Single(r => r.MessageId == failingMessageId).SentAt.Should().BeNull();
        rows.Single(r => r.MessageId == okMessageId).SentAt.Should().NotBeNull();
    }

    private sealed class SelectivelyFailingPublisher : IQuoteEventPublisher
    {
        private readonly string _failOnMessageId;

        public SelectivelyFailingPublisher(string failOnMessageId) => _failOnMessageId = failOnMessageId;

        public Task PublishAsync<T>(T payload, string eventType, string messageId, CancellationToken cancellationToken = default)
        {
            if (messageId == _failOnMessageId)
            {
                throw new InvalidOperationException("Simulated transient broker failure for this one message.");
            }

            return Task.CompletedTask;
        }
    }
}
