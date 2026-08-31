using QuotesApi.Services;

namespace QuotesApi.Models;

/// <summary>
/// A person who can sign in. Owns the quotes they create.
/// </summary>
public class User
{
    public const int MaxEmailLength = 256;

    public int Id { get; private set; }

    /// <summary>Normalised: trimmed and lowercased. See <see cref="NormalizeEmail"/>.</summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>
    /// A BCrypt hash, never a password.
    /// </summary>
    /// <remarks>
    /// The entity has no method that accepts a plaintext password and no way to
    /// return one, which is the point: hashing happens in
    /// <see cref="IPasswordHasher"/> before anything reaches this type, so
    /// there is no code path where a plaintext password can be persisted by
    /// accident. A database dump therefore contains no passwords - only hashes
    /// that each cost real time to attack.
    /// </remarks>
    public string PasswordHash { get; private set; } = string.Empty;

    /// <summary>One of <see cref="Roles"/>. Decided at registration, not sent by the client.</summary>
    public string Role { get; private set; } = Roles.User;

    public DateTime CreatedAt { get; private set; }

    private User() { } // Required for EF Core

    /// <summary>
    /// Creates a user from an already-hashed password.
    /// </summary>
    /// <param name="createdAt">
    /// Passed in rather than read from the ambient clock, for the same reason
    /// <see cref="Quote.Create"/> takes it: an entity that calls
    /// DateTime.UtcNow itself can never be asserted against exactly.
    /// </param>
    public static User Create(string email, string passwordHash, string role, DateTimeOffset createdAt)
    {
        var normalised = NormalizeEmail(email);

        if (string.IsNullOrWhiteSpace(normalised) || normalised.Length > MaxEmailLength)
            throw new InvalidOperationException($"Email must be between 1 and {MaxEmailLength} characters.");

        // Not a full RFC 5322 parse - just enough to reject the shapes that are
        // obviously not an address. The DTO's [EmailAddress] annotation is the
        // real gate; this is the domain refusing to hold something nonsensical
        // even if it is constructed directly by a test or a seeder.
        if (!normalised.Contains('@') || normalised.StartsWith('@') || normalised.EndsWith('@'))
            throw new InvalidOperationException("Email must contain a local part and a domain.");

        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new InvalidOperationException("Password hash cannot be empty.");

        if (role != Roles.User && role != Roles.Admin)
            throw new InvalidOperationException($"Role must be '{Roles.User}' or '{Roles.Admin}'.");

        return new User
        {
            Email = normalised,
            PasswordHash = passwordHash,
            Role = role,
            CreatedAt = createdAt.UtcDateTime
        };
    }

    /// <summary>Convenience overload for application code that already has the clock injected.</summary>
    public static User Create(string email, string passwordHash, string role, IClock clock)
        => Create(email, passwordHash, role, clock.UtcNow);

    /// <summary>
    /// Trims and lowercases, so that one person cannot end up with two accounts.
    /// </summary>
    /// <remarks>
    /// Without this, "Ada@Example.com" and "ada@example.com" are two different
    /// rows to a case-sensitive unique index, and the second registration
    /// succeeds instead of being rejected as a duplicate - after which signing
    /// in depends on capitalising your own address the same way twice.
    /// Invariant culture, not the current one: under a Turkish locale
    /// ToLower() maps 'I' to a dotless 'i', so the same address normalises
    /// differently depending on which machine the server happens to run on.
    /// </remarks>
    public static string NormalizeEmail(string email)
        => (email ?? string.Empty).Trim().ToLowerInvariant();
}
