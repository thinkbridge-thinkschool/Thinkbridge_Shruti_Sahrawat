using System.Net;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace QuotesApi.Resilience;

/// <summary>
/// What the retry and the circuit breaker both agree counts as a failure.
/// </summary>
/// <remarks>
/// Written out rather than left to each strategy's default, because the retry
/// and the breaker sharing one definition is a correctness property, not a
/// tidiness one. If the breaker handled a status the retry ignored, the
/// breaker would accumulate failures for calls the caller saw succeed and
/// would eventually open against a healthy dependency. If the retry handled a
/// status the breaker ignored, the breaker would never see the failures the
/// retry was busy hiding and would never open at all. One predicate, both
/// strategies.
///
/// The set itself is the usual transient list, and each entry is here for a
/// reason rather than by convention:
///
/// <list type="bullet">
/// <item><b>5xx</b> - the server said it failed. Nothing about the request is
/// known to be wrong, so the same request may work later.</item>
/// <item><b>408 Request Timeout</b> and <b>429 Too Many Requests</b> - the
/// server is explicitly asking for a later attempt.</item>
/// <item><b>HttpRequestException</b> - DNS, connection refused, connection
/// reset. The request may never have arrived.</item>
/// <item><b>TimeoutRejectedException</b> - the attempt timeout fired. Note
/// what this one does not tell us: whether the dependency processed the
/// request anyway. That ambiguity is exactly why RetryEligibility exists.</item>
/// </list>
///
/// 4xx other than 408 and 429 are deliberately absent. A 400 or a 404 is the
/// dependency saying the request is wrong, and repeating a wrong request four
/// times produces four wrong answers more slowly.
///
/// <see cref="BrokenCircuitException"/> is excluded explicitly. It is not a
/// failure of the dependency, it is the breaker's own decision, and letting
/// the retry handle it would mean spending the caller's remaining budget on
/// attempts that never leave the process.
/// </remarks>
public static class TransientHttpFailure
{
    public static bool Matches(Outcome<HttpResponseMessage> outcome)
    {
        if (outcome.Exception is not null)
        {
            return outcome.Exception switch
            {
                BrokenCircuitException => false,
                HttpRequestException => true,
                TimeoutRejectedException => true,
                _ => false,
            };
        }

        var response = outcome.Result;

        if (response is null)
        {
            return false;
        }

        return (int)response.StatusCode >= 500
            || response.StatusCode == HttpStatusCode.RequestTimeout
            || response.StatusCode == HttpStatusCode.TooManyRequests;
    }
}
