using QuotesApi.Models;

namespace QuotesApi.Services;

/// <summary>
/// Mints the access token a signed-in user presents on later requests.
/// </summary>
public interface ITokenService
{
    /// <summary>
    /// A signed JWT carrying the user's id, email and role.
    /// </summary>
    /// <remarks>
    /// Takes the entity, not an id and a role separately. Two loose arguments
    /// of compatible types are two arguments that can be passed in the wrong
    /// order - and a token minted with someone's role in the id position is a
    /// bug no compiler catches and no test notices unless it happens to assert
    /// on the exact claim values.
    /// </remarks>
    string CreateAccessToken(User user);

    /// <summary>How long the tokens this service issues remain valid.</summary>
    TimeSpan AccessTokenLifetime { get; }
}
