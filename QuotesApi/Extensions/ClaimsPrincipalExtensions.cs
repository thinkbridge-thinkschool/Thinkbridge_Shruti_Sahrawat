using System.Security.Claims;
using QuotesApi.Models;

namespace QuotesApi.Extensions;

/// <summary>
/// Reads the two things every quotes endpoint needs from the caller's token:
/// who they are, and whether they are an admin.
/// </summary>
/// <remarks>
/// One place that knows which claim carries the user id. Endpoints that each
/// dig the claim out themselves are endpoints that can each get it subtly
/// wrong - and the failure mode of reading the wrong claim here is not an
/// error, it is one user quietly operating on another user's rows.
/// </remarks>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The signed-in user's id, or null when the token carries no usable one.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception, and null rather than 0. Zero is a value
    /// that compares equal to a real column default, so a bug that let it
    /// through would match rows rather than matching nothing - failing open,
    /// which is the one direction an authorisation check must never fail.
    ///
    /// [Authorize] guarantees the token was signed by us and has not expired.
    /// It does not guarantee the claims inside are the ones we expect, so
    /// callers still have to handle null - a token signed with our key but
    /// carrying no id is not a caller we can attribute rows to.
    /// </remarks>
    public static int? UserId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(raw, out var id) && id > 0 ? id : null;
    }

    public static bool IsAdmin(this ClaimsPrincipal principal)
        => principal.IsInRole(Roles.Admin);

    /// <summary>
    /// Which owner id to filter a listing by: null for an admin, meaning "do
    /// not filter", and the caller's own id for everyone else.
    /// </summary>
    /// <param name="userId">
    /// The caller's already-resolved id. Taken as a parameter rather than read
    /// from the principal inside this method on purpose: reading it here would
    /// mean a malformed token produced a null id, which this method would then
    /// return as "do not filter" - showing every quote in the database to
    /// whoever presented it. Requiring the caller to resolve the id first
    /// makes that path impossible to reach by accident.
    /// </param>
    public static int? OwnerFilterFor(this ClaimsPrincipal principal, int userId)
        => principal.IsAdmin() ? null : userId;

    /// <summary>
    /// Whether this caller may read or delete a quote owned by
    /// <paramref name="ownerId"/>.
    /// </summary>
    /// <remarks>
    /// A quote with no owner is one created before accounts existed. Admin
    /// only: handing legacy rows to whichever user asked first would be
    /// inventing an owner, and hiding them from everyone would quietly orphan
    /// data that is still in the database.
    /// </remarks>
    public static bool CanAccessQuoteOwnedBy(this ClaimsPrincipal principal, int? ownerId, int userId)
        => principal.IsAdmin() || (ownerId is not null && ownerId == userId);
}
