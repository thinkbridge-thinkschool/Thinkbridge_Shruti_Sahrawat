using System.ComponentModel.DataAnnotations;

namespace QuotesApi.Extensions;

/// <summary>
/// Runs DataAnnotations over a request body and shapes the failures into the
/// ValidationProblemDetails the client already knows how to read.
/// </summary>
/// <remarks>
/// Minimal APIs do not validate a bound body the way MVC model binding does -
/// the attributes on the DTO are inert unless something calls Validator
/// explicitly. This is that something, extracted so the quotes endpoints and
/// the auth endpoints produce byte-identical error shapes; the Angular client
/// maps `errors` per field (see error-mapping.ts) and a second, slightly
/// different shape would silently fall through to its generic handler.
/// </remarks>
public static class RequestValidation
{
    /// <returns>
    /// True when the request is valid. When false, <paramref name="problem"/>
    /// holds the 400 to return.
    /// </returns>
    public static bool TryValidate(object request, out IResult problem)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(request);

        if (Validator.TryValidateObject(request, context, results, validateAllProperties: true))
        {
            problem = Results.Empty;
            return true;
        }

        var errors = results
            .SelectMany(r => r.MemberNames.DefaultIfEmpty(""), (r, member) => (member, r.ErrorMessage))
            .GroupBy(x => x.member)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "Invalid value").ToArray());

        problem = Results.ValidationProblem(errors);
        return false;
    }
}
