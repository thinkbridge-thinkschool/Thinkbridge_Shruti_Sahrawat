using Asp.Versioning;
using Microsoft.OpenApi;

namespace QuotesApi.Extensions;

/// <summary>
/// Day 27. The two things that make an API surface reviewable: a described
/// contract, and a version on it.
/// </summary>
public static class ApiSurfaceExtensions
{
    /// <summary>
    /// OpenAPI with the bearer scheme declared, so the document says how to
    /// authenticate rather than leaving a reader to infer it from a 401.
    /// </summary>
    /// <remarks>
    /// Until today this API had no OpenAPI document at all. That is not a
    /// security hole by itself - there is nothing to leak - but it means the
    /// surface was never reviewable in one place, and the anonymous
    /// diagnostic endpoints found in this day's threat model are exactly the
    /// kind of thing a generated contract makes obvious at a glance.
    /// </remarks>
    public static IServiceCollection AddQuotesOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Quotes API",
                    Version = "v1",
                    Description =
                        "Quotes capstone. Every endpoint outside /api/auth/register and " +
                        "/api/auth/login requires a bearer token."
                };

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Paste the access token from POST /api/auth/login."
                };

                return Task.CompletedTask;
            });
        });

        return services;
    }

    /// <summary>
    /// API versioning that does not break a single existing caller.
    /// </summary>
    /// <remarks>
    /// The constraint here is the one that governs this whole repository: no
    /// loss in any task. The Angular client, the k6 scripts in perf/, the
    /// PowerShell in scripts/ and every integration test call the current
    /// unversioned paths, so versioning by moving routes to /v1 would break
    /// all of them at once for no security gain.
    ///
    /// So the version travels in a query string or a header, and
    /// AssumeDefaultVersionWhenUnspecified means a caller that sends neither
    /// gets v1 - which is what every existing caller is already asking for
    /// without knowing it. ReportApiVersions adds an api-supported-versions
    /// response header, so a client can discover what exists without reading
    /// the source.
    ///
    /// What this buys is the ability to ship a v2 that changes a response
    /// shape without a flag day. What it does not buy is protection from
    /// anything; versioning is on this day's list because an unversioned
    /// public surface cannot be changed safely, and "cannot be changed
    /// safely" eventually means "cannot be patched safely".
    /// </remarks>
    public static IServiceCollection AddQuotesApiVersioning(this IServiceCollection services)
    {
        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
            options.ApiVersionReader = ApiVersionReader.Combine(
                new QueryStringApiVersionReader("api-version"),
                new HeaderApiVersionReader("X-Api-Version"));
        });

        return services;
    }
}
