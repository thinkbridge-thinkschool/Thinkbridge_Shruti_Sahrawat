using System.ComponentModel.DataAnnotations;

namespace QuotesApi.Models;

/// <summary>
/// Shared password rules. Applied at the edge, before anything is hashed -
/// the entity only ever sees a hash and so cannot enforce these itself.
/// </summary>
public static class PasswordRules
{
    public const int MinLength = 8;

    /// <summary>
    /// 72, because that is where BCrypt stops reading.
    /// </summary>
    /// <remarks>
    /// BCrypt hashes at most 72 bytes and silently ignores everything after.
    /// Without this cap the API would accept a 200-character passphrase and
    /// then authenticate anyone who typed the first 72 characters of it - a
    /// weakening of security that produces no error and no log line. Rejecting
    /// the input is honest; truncating it quietly is not.
    /// </remarks>
    public const int MaxLength = 72;
}

public class RegisterRequest
{
    [Required, EmailAddress, StringLength(User.MaxEmailLength, MinimumLength = 3)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(PasswordRules.MaxLength, MinimumLength = PasswordRules.MinLength)]
    public string Password { get; set; } = string.Empty;
}

public class LoginRequest
{
    [Required, EmailAddress, StringLength(User.MaxEmailLength, MinimumLength = 3)]
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Deliberately not length-validated.
    /// </summary>
    /// <remarks>
    /// Login must not tell an attacker anything the credentials themselves do
    /// not. A minimum-length rule here would answer "that is not even a valid
    /// password" for some inputs and "invalid credentials" for others, which
    /// leaks a little of the password policy and gives a script one more way
    /// to narrow its guesses. Every wrong password gets the same 401.
    /// </remarks>
    [Required]
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// What register and login both hand back.
/// </summary>
/// <remarks>
/// camelCase on the wire (accessToken, expiresIn), because that is what
/// ASP.NET Core's web defaults already produce for every other response this
/// API returns and what quotes.ts on the client already expects. OrderRefactor's
/// AuthController returns OAuth-style snake_case instead; matching that here
/// would make this one endpoint the odd one out in its own API, which is the
/// more confusing of the two inconsistencies.
///
/// ExpiresIn is seconds, not an absolute instant. A client comparing an
/// absolute expiry against its own clock is comparing against a clock that may
/// be wrong by hours; a duration is interpreted against the moment the response
/// actually arrived.
/// </remarks>
public record AuthResponse(string AccessToken, int ExpiresIn, UserResponse User);

/// <summary>
/// The current user, as the client is allowed to see them. No password hash,
/// no CreatedAt - a response DTO exists precisely so that adding a column to
/// the entity does not silently start publishing it.
/// </summary>
public record UserResponse(int Id, string Email, string Role)
{
    public static UserResponse FromEntity(User user) => new(user.Id, user.Email, user.Role);
}
