using QuotesApi.Services;

namespace QuotesApi.Models;

public class Quote
{
    public int Id { get; private set; }
    public string Author { get; private set; } = string.Empty;
    public string Text { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }
    public bool IsDeleted { get; private set; }

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
    /// </remarks>
    public static Quote Create(string author, string text, DateTimeOffset createdAt)
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
            IsDeleted = false
        };
    }

    /// <summary>
    /// Convenience overload for application code that already has the clock injected.
    /// </summary>
    public static Quote Create(string author, string text, IClock clock)
        => Create(author, text, clock.UtcNow);

    public void SoftDelete()
    {
        IsDeleted = true;
    }
}
