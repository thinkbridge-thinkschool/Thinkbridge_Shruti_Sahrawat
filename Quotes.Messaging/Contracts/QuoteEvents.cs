namespace Quotes.Messaging.Contracts;

/// <summary>
/// The event type names that travel on the wire, as a message's
/// <c>eventType</c> application property and as its <c>Subject</c>.
/// </summary>
/// <remarks>
/// These are constants rather than an enum because a subscription's SQL filter
/// matches them as strings inside the broker
/// (<c>user.eventType = 'QuoteCreated'</c>), and a filter that silently stops
/// matching because someone renamed an enum member is a failure with no
/// compiler error and no runtime exception - messages simply stop arriving.
/// Keeping the literal in one named place at least makes the coupling visible.
/// </remarks>
public static class QuoteEventTypes
{
    public const string QuoteCreated = "QuoteCreated";
    public const string QuoteDeleted = "QuoteDeleted";
}

/// <summary>Published when a quote is added.</summary>
public sealed record QuoteCreated(int QuoteId, string Author, string Text, DateTimeOffset OccurredAt);

/// <summary>Published when a quote is soft-deleted.</summary>
public sealed record QuoteDeleted(int QuoteId, DateTimeOffset OccurredAt);
