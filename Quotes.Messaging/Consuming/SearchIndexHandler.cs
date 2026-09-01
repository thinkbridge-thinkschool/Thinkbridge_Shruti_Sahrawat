using System.Text.Json;
using Quotes.Messaging.Contracts;
using Quotes.Messaging.Data;

namespace Quotes.Messaging.Consuming;

/// <summary>
/// The <c>search-indexer</c> subscription's handler: keeps a searchable
/// projection of every created quote.
/// </summary>
/// <remarks>
/// This is the consumer that runs as competing consumers. Several worker
/// instances read the same subscription and each message goes to exactly one of
/// them, so throughput scales with instance count without any of them
/// coordinating.
/// </remarks>
public sealed class SearchIndexHandler : IQuoteEventHandler
{
    /// <summary>
    /// Author value that makes this handler fail every time it is seen.
    /// </summary>
    /// <remarks>
    /// A simulation, and labelled as one rather than hidden behind something
    /// that looks accidental. Proving the delivery-count path needs a failure
    /// that recurs identically on every attempt; a genuinely flaky dependency
    /// would sometimes succeed on the retry and prove nothing repeatable. It
    /// throws an ordinary exception, not PoisonMessageException, precisely
    /// because the point is to exercise the <em>other</em> route to the
    /// dead-letter queue: retry, retry, retry, then the broker gives up.
    /// </remarks>
    public const string AlwaysFailsAuthor = "ALWAYS-FAILS";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Consumer => "search-indexer";

    public async Task ApplyAsync(IncomingMessage message, MessagingDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var quote = Parse(message);

        if (quote.Author == AlwaysFailsAuthor)
        {
            throw new InvalidOperationException(
                "Simulated downstream index failure - this message will fail every delivery.");
        }

        // Upsert rather than Add. A projection is a rebuildable copy, not a
        // record of what happened: replaying the topic to rebuild it from
        // scratch must converge on the same state rather than blow up on the
        // first quote it has already seen.
        var existing = await db.IndexedQuotes.FindAsync(new object[] { quote.QuoteId }, cancellationToken);
        if (existing is null)
        {
            db.IndexedQuotes.Add(new IndexedQuote
            {
                QuoteId = quote.QuoteId,
                Author = quote.Author,
                Text = quote.Text,
                IndexedAt = now,
            });
        }
        else
        {
            existing.Author = quote.Author;
            existing.Text = quote.Text;
            existing.IndexedAt = now;
        }
    }

    private static QuoteCreated Parse(IncomingMessage message)
    {
        if (message.EventType != QuoteEventTypes.QuoteCreated)
        {
            // The broker's SQL filter should already have kept this off the
            // subscription. Arriving here means the filter and this code
            // disagree, which is a deployment mistake rather than a bad
            // message - but it still cannot be processed, so it goes to the
            // dead-letter queue where someone will see it, instead of being
            // silently dropped.
            throw new PoisonMessageException(
                "UnexpectedEventType",
                $"search-indexer received '{message.EventType}', which its subscription filter should have excluded.");
        }

        QuoteCreated? quote;
        try
        {
            quote = JsonSerializer.Deserialize<QuoteCreated>(message.Body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException("MalformedJson", ex.Message);
        }

        if (quote is null)
        {
            throw new PoisonMessageException("MalformedJson", "Body deserialised to null.");
        }

        if (quote.QuoteId <= 0)
        {
            throw new PoisonMessageException("InvalidQuoteId", $"QuoteId was {quote.QuoteId}; must be positive.");
        }

        if (string.IsNullOrWhiteSpace(quote.Author))
        {
            throw new PoisonMessageException("MissingAuthor", "Author was empty.");
        }

        return quote;
    }
}
