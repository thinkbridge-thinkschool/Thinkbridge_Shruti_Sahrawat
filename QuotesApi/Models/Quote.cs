using QuotesApi.Services;

namespace QuotesApi.Models;

public class Quote
{
    public int Id { get; private set; }
    public string Author { get; private set; } = string.Empty;
    public string Text { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }
    public bool IsDeleted { get; private set; }

    /// <summary>
    /// The <see cref="User"/> who created this quote, or null for a quote that
    /// predates accounts existing.
    /// </summary>
    /// <remarks>
    /// Nullable rather than backfilled to some placeholder owner. Every quote
    /// already in the database was created when the API had no concept of a
    /// user, and assigning those rows to whichever account registers first
    /// would be inventing an owner the data never had. Null means exactly what
    /// it looks like - nobody owns this - and only an admin can see or delete
    /// one, so the rows are neither handed to a stranger nor silently orphaned.
    /// </remarks>
    public int? OwnerId { get; private set; }

    private Quote() { } // Required for EF Core

    /// <summary>
    /// Creates a quote at a caller-supplied instant.
    /// </summary>
    /// <remarks>
    /// The timestamp is a parameter, not something the entity goes and finds.
    /// An entity that reads DateTime.UtcNow itself cannot be asserted against
    /// exactly - every test either waits or settles for "close to now", and no
    /// test can express "what happens to a quote created last year". Handing
    /// the time in makes both trivial.
    ///
    /// This is also why the domain takes a DateTimeOffset rather than an
    /// IClock: the entity needs the value, not the service that produces it.
    /// Depending on the service would pull a DI concern into a type that has
    /// no other dependencies.
    ///
    /// ownerId is optional rather than required, which is a deliberate
    /// trade. Making it required would have the compiler point at every
    /// caller that has not been told about owners yet - genuinely safer for a
    /// field this one matters - but the only callers without an owner are the
    /// seventeen existing tests, none of which are about ownership, and
    /// rewriting all of them to pass a value they do not care about would bury
    /// the change that does matter. The endpoint is where a real request gets
    /// its owner, and QuoteEndpointsTests asserts that it does.
    /// </remarks>
    public static Quote Create(string author, string text, DateTimeOffset createdAt, int? ownerId = null)
    {
        if (string.IsNullOrWhiteSpace(author) || author.Trim().Length > 200)
            throw new InvalidOperationException("Author must be between 1 and 200 characters.");

        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > 1000)
            throw new InvalidOperationException("Quote text must be between 1 and 1000 characters.");

        return new Quote
        {
            Author = author.Trim(),
            Text = text.Trim(),
            CreatedAt = createdAt.UtcDateTime,
            IsDeleted = false,
            OwnerId = ownerId
        };
    }

    /// <summary>
    /// Convenience overload for application code that already has the clock injected.
    /// </summary>
    public static Quote Create(string author, string text, IClock clock, int? ownerId = null)
        => Create(author, text, clock.UtcNow, ownerId);

    public void SoftDelete()
    {
        IsDeleted = true;
    }
}
