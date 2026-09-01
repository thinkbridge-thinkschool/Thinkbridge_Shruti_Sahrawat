using Quotes.Messaging;
using Quotes.Messaging.Consuming;
using Quotes.Messaging.Contracts;
using Quotes.Messaging.Publishing;

namespace Quotes.Worker;

/// <summary>
/// Publishes the message set the exercise is proven with.
/// </summary>
/// <remarks>
/// Every message here exists to force one specific behaviour to show itself.
/// Nothing is filler: six ordinary events so competing consumers have enough
/// work to visibly divide, one deliberate replay, one event the indexer's
/// filter must exclude, and two different kinds of failure that must reach the
/// dead-letter queue by two different routes.
/// </remarks>
public static class DemoPublisher
{
    public static async Task RunAsync(IServiceProvider services)
    {
        var settings = services.GetRequiredService<ServiceBusSettings>();
        var client = services.GetRequiredService<Azure.Messaging.ServiceBus.ServiceBusClient>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("DemoPublisher");

        await using var publisher = new ServiceBusQuoteEventPublisher(
            client, settings,
            services.GetRequiredService<ILoggerFactory>().CreateLogger<ServiceBusQuoteEventPublisher>());

        // Fixed instants, not DateTimeOffset.UtcNow. The message id is derived
        // from the event's timestamp, so a fixed instant means re-running this
        // command produces the *same* ids - which is what makes the whole run
        // repeatable and lets the duplicate below be a genuine duplicate rather
        // than a second distinct event that happens to look similar.
        var baseTime = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

        Console.WriteLine();
        Console.WriteLine("== 1. Six ordinary QuoteCreated events ==");
        Console.WriteLine("   Both subscriptions receive all six. On search-indexer they are divided");
        Console.WriteLine("   between whichever worker instances are running.");

        var quotes = new (int Id, string Author, string Text)[]
        {
            (101, "Ada Lovelace",      "That brain of mine is something more than merely mortal."),
            (102, "Grace Hopper",      "The most damaging phrase is: we have always done it this way."),
            (103, "Alan Turing",       "Those who can imagine anything, can create the impossible."),
            (104, "Barbara Liskov",    "There is a huge difference between a program and a system."),
            (105, "Edsger Dijkstra",   "Simplicity is prerequisite for reliability."),
            (106, "Margaret Hamilton", "There was no choice but to be pioneers."),
        };

        foreach (var (id, author, text) in quotes)
        {
            var occurredAt = baseTime.AddSeconds(id);
            await PublishCreated(publisher, id, author, text, occurredAt);
        }

        Console.WriteLine();
        Console.WriteLine("== 2. A replay of the first event, byte-identical message id ==");
        Console.WriteLine("   Broker-side duplicate detection is deliberately OFF, so this really is");
        Console.WriteLine("   delivered a second time. The consumer's ledger is what has to catch it.");

        await PublishCreated(publisher, quotes[0].Id, quotes[0].Author, quotes[0].Text, baseTime.AddSeconds(quotes[0].Id));

        Console.WriteLine();
        Console.WriteLine("== 3. A QuoteDeleted event ==");
        Console.WriteLine("   audit-log's rule is a catch-all so it receives this; search-indexer's SQL");
        Console.WriteLine("   filter is eventType = 'QuoteCreated', so the broker never delivers it there.");

        var deletedAt = baseTime.AddSeconds(200);
        await publisher.PublishAsync(
            new QuoteDeleted(103, deletedAt),
            QuoteEventTypes.QuoteDeleted,
            QuoteEventIds.For(QuoteEventTypes.QuoteDeleted, 103, deletedAt));

        Console.WriteLine();
        Console.WriteLine("== 4. Poison: a permanently invalid payload ==");
        Console.WriteLine("   QuoteId is negative, which can never become valid. search-indexer");
        Console.WriteLine("   dead-letters it on the FIRST delivery rather than retrying.");
        Console.WriteLine("   audit-log accepts it - proof the two subscriptions are truly independent.");

        var poisonAt = baseTime.AddSeconds(300);
        await publisher.PublishAsync(
            new QuoteCreated(-1, "Nobody", "This payload can never be indexed.", poisonAt),
            QuoteEventTypes.QuoteCreated,
            QuoteEventIds.For(QuoteEventTypes.QuoteCreated, -1, poisonAt));

        Console.WriteLine();
        Console.WriteLine("== 5. Poison: a handler that fails every single delivery ==");
        Console.WriteLine($"   Author is '{SearchIndexHandler.AlwaysFailsAuthor}', which throws an ordinary");
        Console.WriteLine("   exception - so it is abandoned and retried, and the BROKER dead-letters it");
        Console.WriteLine("   once DeliveryCount passes MaxDeliveryCount (3). Give it a few seconds.");

        var failsAt = baseTime.AddSeconds(400);
        await publisher.PublishAsync(
            new QuoteCreated(999, SearchIndexHandler.AlwaysFailsAuthor, "Retried, then dead-lettered.", failsAt),
            QuoteEventTypes.QuoteCreated,
            QuoteEventIds.For(QuoteEventTypes.QuoteCreated, 999, failsAt));

        Console.WriteLine();
        Console.WriteLine("Published 10 messages. Watch the worker windows, then run:");
        Console.WriteLine("  dotnet run -- report   (projections + idempotency ledger)");
        Console.WriteLine("  dotnet run -- dlq      (what landed in the dead-letter queues)");
        Console.WriteLine();

        logger.LogInformation("Demo publish complete.");
    }

    private static Task PublishCreated(
        IQuoteEventPublisher publisher, int id, string author, string text, DateTimeOffset occurredAt)
        => publisher.PublishAsync(
            new QuoteCreated(id, author, text, occurredAt),
            QuoteEventTypes.QuoteCreated,
            QuoteEventIds.For(QuoteEventTypes.QuoteCreated, id, occurredAt));
}
