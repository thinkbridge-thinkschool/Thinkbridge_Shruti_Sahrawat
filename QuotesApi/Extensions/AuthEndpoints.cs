using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using QuotesApi.Configuration;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;

namespace QuotesApi.Extensions;

/// <summary>
/// Register, sign in, and "who am I".
/// </summary>
/// <remarks>
/// Minimal APIs rather than a controller, matching MapQuoteEndpoints - these
/// sit directly alongside the quotes endpoints they exist to protect, and
/// CollectionsController's MVC style is the outlier in this project rather
/// than the convention.
/// </remarks>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", async (
            RegisterRequest request,
            IUserRepository users,
            IPasswordHasher hasher,
            ITokenService tokens,
            IOptions<AuthOptions> authOptions,
            IClock clock,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!RequestValidation.TryValidate(request, out var problem)) return problem;

            var email = User.NormalizeEmail(request.Email);
            var logger = loggerFactory.CreateLogger("QuotesApi.Auth");

            if (await users.FindByEmailAsync(email, ct) is not null)
            {
                return Results.Conflict(new ProblemDetails
                {
                    Title = "Email already registered",
                    Status = StatusCodes.Status409Conflict,
                    Detail = "An account with that email already exists. Sign in instead."
                });
            }

            // The role is decided here, from configuration, and never read from
            // the request body. A "role" field a client could send is a client
            // that can make itself an admin - the single most common way a
            // homegrown auth system is broken, and it looks perfectly ordinary
            // in a code review until someone tries it.
            var isAdmin = authOptions.Value.AdminEmails
                .Any(configured => User.NormalizeEmail(configured) == email);
            var role = isAdmin ? Roles.Admin : Roles.User;

            var user = User.Create(email, hasher.Hash(request.Password), role, clock);
            var saved = await users.AddAsync(user, ct);

            if (saved is null)
            {
                // Lost the race against a concurrent registration of the same
                // address. Same answer as the check above - the outcome the
                // caller cares about is identical.
                return Results.Conflict(new ProblemDetails
                {
                    Title = "Email already registered",
                    Status = StatusCodes.Status409Conflict,
                    Detail = "An account with that email already exists. Sign in instead."
                });
            }

            if (isAdmin)
            {
                logger.LogInformation("Registered user {UserId} as an admin (email matched Auth:AdminEmails)", saved.Id);
            }

            // 201 with /api/auth/me as the location: that genuinely is the URL
            // where the thing just created can be read back, for the caller now
            // holding this token.
            return Results.Created("/api/auth/me", BuildAuthResponse(saved, tokens));
        })
        .AllowAnonymous()
        .WithName("Register");

        group.MapPost("/login", async (
            LoginRequest request,
            IUserRepository users,
            IPasswordHasher hasher,
            ITokenService tokens,
            CancellationToken ct) =>
        {
            if (!RequestValidation.TryValidate(request, out var problem)) return problem;

            var email = User.NormalizeEmail(request.Email);
            var user = await users.FindByEmailAsync(email, ct);

            if (user is null)
            {
                // Hash the supplied password and throw the result away.
                //
                // Not busywork: BCrypt at work factor 12 takes a few hundred
                // milliseconds, so an endpoint that skips it for unknown
                // addresses answers those noticeably faster than it answers a
                // known address with a wrong password. That timing difference
                // is a working "does this person have an account here?" oracle
                // - answerable at scale, against a list of email addresses,
                // without ever guessing a password. Doing the same work in both
                // branches costs one slow response on a path nobody legitimate
                // takes twice.
                hasher.Hash(request.Password);
                return InvalidCredentials();
            }

            if (!hasher.Verify(request.Password, user.PasswordHash))
            {
                return InvalidCredentials();
            }

            return Results.Ok(BuildAuthResponse(user, tokens));
        })
        .AllowAnonymous()
        .WithName("Login");

        group.MapGet("/me", async (
            ClaimsPrincipal principal,
            IUserRepository users,
            CancellationToken ct) =>
        {
            var userId = principal.UserId();
            if (userId is null) return Results.Unauthorized();

            var user = await users.GetByIdAsync(userId.Value, ct);

            // A valid, unexpired token for an account that no longer exists.
            // 401, not 404: the question "who am I" has no answer, and the
            // client's correct response is to sign in again rather than to
            // treat it as a missing page.
            return user is null
                ? Results.Unauthorized()
                : Results.Ok(UserResponse.FromEntity(user));
        })
        .RequireAuthorization()
        .WithName("Me");

        return app;
    }

    private static AuthResponse BuildAuthResponse(User user, ITokenService tokens) => new(
        tokens.CreateAccessToken(user),
        (int)tokens.AccessTokenLifetime.TotalSeconds,
        UserResponse.FromEntity(user));

    /// <summary>
    /// One answer for every way sign-in can fail.
    /// </summary>
    /// <remarks>
    /// "No such account" and "wrong password" are the same 401 with the same
    /// body. Telling them apart is a courtesy to the person who mistyped their
    /// address and a gift to anyone testing a list of addresses to find out
    /// which ones are registered here.
    /// </remarks>
    private static IResult InvalidCredentials() => Results.Json(
        new ProblemDetails
        {
            Title = "Invalid credentials",
            Status = StatusCodes.Status401Unauthorized,
            Detail = "That email and password combination was not recognised."
        },
        statusCode: StatusCodes.Status401Unauthorized);
}
