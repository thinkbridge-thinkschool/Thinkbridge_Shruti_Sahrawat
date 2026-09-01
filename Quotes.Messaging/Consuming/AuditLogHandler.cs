using System.Text.Json;
using Quotes.Messaging.Data;

namespace Quotes.Messaging.Consuming;

/// <summary>
/// The <c>audit-log</c> subscription's handler: an append-only record of every
/// event that crossed the topic.
/// </summary>
/// <remarks>
/// This handler exists to make the fan-out real rather than decorative. It
/// reads a <em>different subscription</em> on the same topic, so it receives
/// its own copy of every message the search indexer receives - and, because its
/// rule is a catch-all where the indexer's filters on event type, it also
/// receives the ones the indexer never sees. Neither consumer knows the other
/// exists; adding a third would need no change to the publisher.
///
/// It deliberately parses loosely - only the fields it actually records -
/// where the search indexer parses strictly. An auditor that rejects an event
/// because it gained a field it does not care about is an auditor with gaps in
/// it exactly when the schema is changing.
/// </remarks>
public sealed class AuditLogHandler : IQuoteEventHandler
{
    public string Consumer => "audit-log";

    public Task ApplyAsync(IncomingMessage message, MessagingDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        int quoteId;
        try
        {
            using var document = JsonDocument.Parse(message.Body);
            quoteId = document.RootElement.TryGetProperty("quoteId", out var idElement)
                      && idElement.TryGetInt32(out var parsed)
                ? parsed
                : 0;
        }
        catch (JsonException ex)
        {
            // Even a permissive auditor cannot record what it cannot read.
            throw new PoisonMessageException("MalformedJson", ex.Message);
        }

        db.AuditEntries.Add(new AuditEntry
        {
            EventType = message.EventType,
            QuoteId = quoteId,
            Detail = Truncate(message.Body, 1000),
            RecordedAt = now,
        });

        return Task.CompletedTask;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}
