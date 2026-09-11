using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace QuotesApi.Extensions;

/// <summary>
/// Day 27. Two limits: a generous global one, and a strict one on the
/// anonymous auth endpoints.
/// </summary>
/// <remarks>
/// The threat this closes is credential stuffing. `/api/auth/login` is
/// anonymous by necessity and hashes with BCrypt, which makes each guess
/// expensive for the server as well as the attacker - so an unlimited login
/// endpoint is both the way in and, at enough concurrency, the way to exhaust
/// the CPU of everything else.
///
/// The two limits are deliberately different in kind, not just in number:
///
/// <b>Global: 300/minute per client.</b> High enough that no honest caller
/// reaches it - the Angular client's busiest screen issues a handful of
/// requests - and low enough to blunt a scraper. This is a safety net, not a
/// quota.
///
/// <b>Auth: 10/minute per client.</b> A human signing in needs two or three
/// attempts, not ten. This is the one doing real work.
///
/// <b>On partitioning, honestly.</b> Both partition on the client IP, and
/// behind Container Apps that address is only the real caller because
/// UseForwardedHeaders (see Program.cs) turns X-Forwarded-For back into
/// RemoteIpAddress. That header is attacker-controlled on any path that does
/// not go through the ingress, so this is a speed bump against a distributed
/// attacker who can vary source addresses - it raises the cost of guessing,
/// it does not make guessing impossible. The control that actually stops a
/// determined attacker is account lockout, which this app does not have and
/// which is named as a gap in the threat model rather than quietly implied by
/// the presence of a rate limiter.
/// </remarks>
public static class RateLimitingExtensions
{
    /// <summary>Policy name for the anonymous auth endpoints.</summary>
    public const string AuthPolicy = "auth";

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // 429, not the default 503. A client that is being rate limited has
            // not hit a broken server, and the two deserve different retries -
            // 503 invites the caller's own retry policy to hammer straight back.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, token) =>
            {
                // Tell the caller when to come back rather than leaving them to
                // guess, so a well-behaved client backs off correctly.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString();
                }

                context.HttpContext.Response.ContentType = "application/problem+json";
                await context.HttpContext.Response.WriteAsync(
                    """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.29","title":"Too many requests","status":429}""",
                    token);
            };

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ClientKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 300,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            options.AddPolicy(AuthPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ClientKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });

        return services;
    }

    // A missing RemoteIpAddress is its own partition rather than sharing one
    // with every other unknown caller - otherwise one unidentifiable client
    // can exhaust the window for all of them.
    private static string ClientKey(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
