using Microsoft.EntityFrameworkCore;
using Quotes.Messaging.Data;

namespace Quotes.Worker;

/// <summary>
/// Prints the consumer-side database: what each handler built, and the
/// idempotency ledger that decided how often each handler ran.
/// </summary>
/// <remarks>
/// The ledger is the interesting half. Because it records which instance
/// processed each message, it is direct evidence for two separate claims that
/// are otherwise only visible as interleaved console output: that competing
/// consumers really did divide one subscription's messages between instances,
/// and that a replayed message did not produce a second effect.
/// </remarks>
public static class StateReporter
{
    public static async Task RunAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var indexed = await db.IndexedQuotes.AsNoTracking().OrderBy(q => q.QuoteId).ToListAsync();
        var audits = await db.AuditEntries.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
        var ledger = await db.ProcessedMessages.AsNoTracking()
            .OrderBy(p => p.Consumer).ThenBy(p => p.MessageId).ToListAsync();

        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"SEARCH INDEX PROJECTION  ({indexed.Count} rows)");
        Console.WriteLine(new string('=', 78));
        foreach (var quote in indexed)
        {
            Console.WriteLine($"  {quote.QuoteId,4}  {quote.Author,-20}  {Shorten(quote.Text, 44)}");
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"AUDIT LOG  ({audits.Count} rows)");
        Console.WriteLine(new string('=', 78));
        foreach (var entry in audits)
        {
            Console.WriteLine($"  {entry.Id,3}  {entry.EventType,-14} quoteId={entry.QuoteId,-5} at {entry.RecordedAt:HH:mm:ss}");
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"IDEMPOTENCY LEDGER  ({ledger.Count} rows)");
        Console.WriteLine(new string('=', 78));

        foreach (var group in ledger.GroupBy(p => p.Consumer))
        {
            Console.WriteLine();
            Console.WriteLine($"  {group.Key}  ({group.Count()} messages processed exactly once)");

            foreach (var row in group)
            {
                Console.WriteLine($"    {row.MessageId,-46}  by {row.ProcessedBy}");
            }

            // The split across instances is the competing-consumer evidence.
            var byInstance = group.GroupBy(p => p.ProcessedBy)
                                  .Select(g => $"{g.Key}={g.Count()}")
                                  .OrderBy(s => s);
            Console.WriteLine($"    -> split across instances: {string.Join(", ", byInstance)}");
        }

        Console.WriteLine();
    }

    private static string Shorten(string value, int max)
        => value.Length <= max ? value : value[..(max - 3)] + "...";
}
