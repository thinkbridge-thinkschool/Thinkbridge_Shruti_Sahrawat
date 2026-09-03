namespace QuotesApi.Resilience;

/// <summary>
/// Decides whether a request may be sent a second time. This is the
/// "idempotent only" half of Day 22's retry, and it is the part of a
/// resilience pipeline most likely to cause the damage it was added to
/// prevent.
/// </summary>
/// <remarks>
/// A retry is not free. The failure a retry responds to is usually a timeout
/// or a dropped connection, and neither of those tells the caller whether the
/// dependency processed the request before the wire went quiet. Retrying a
/// GET that already succeeded costs one wasted round trip. Retrying a POST
/// that already succeeded creates a second order.
///
/// So the gate is on <b>idempotency</b>, per RFC 9110 section 9.2.2: a method
/// is idempotent when sending it twice has the same effect on the server as
/// sending it once. GET, HEAD, OPTIONS and TRACE qualify because they change
/// nothing; PUT and DELETE qualify because they describe an end state rather
/// than a delta. POST and PATCH do not.
///
/// This is deliberately not
/// <c>HttpRetryStrategyOptions.DisableForUnsafeHttpMethods()</c>, the helper
/// the framework ships for roughly this purpose. That helper gates on
/// <b>safety</b>, not idempotency, and the two are different properties:
/// PUT and DELETE are idempotent but not safe, so the built-in helper refuses
/// to retry them. Refusing to retry a dropped DELETE is a correctness loss for
/// no correctness gain - repeating it lands on the same end state. The
/// exercise asked for idempotent, so this implements idempotent.
///
/// The POST escape hatch is Day 20's contract, restated on the client side.
/// The outbox pattern made at-least-once delivery safe by giving every message
/// a stable MessageId and having the consumer deduplicate on it. A POST that
/// carries an <see cref="IdempotencyKeyHeader"/> is making exactly that claim:
/// the server knows how to recognise a repeat. When a caller says so, the
/// retry is safe and this returns true; when it does not, the retry is
/// suppressed and counted.
///
/// Two failure modes are worth naming rather than hiding. This trusts the
/// header - a caller that sends a key against a server that ignores it gets
/// duplicates, and nothing here can detect that. And a request whose body is
/// a non-buffered stream cannot be replayed even when the method is
/// idempotent; the retry will fail on the second attempt with the content
/// already consumed. Neither is a problem for this codebase's small JSON
/// bodies, and both are the first things that would bite a larger one.
/// </remarks>
public static class RetryEligibility
{
    /// <summary>The de facto standard header name for a caller-supplied dedup key.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    private static readonly HashSet<string> IdempotentMethods =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "GET", "HEAD", "OPTIONS", "TRACE", "PUT", "DELETE"
        };

    /// <summary>
    /// True when this request can be safely repeated.
    /// </summary>
    /// <remarks>
    /// A null request returns false rather than true. If the pipeline cannot
    /// tell what it is about to repeat, the answer that cannot corrupt data is
    /// "do not repeat it" - a missed retry degrades availability, an unsafe
    /// retry degrades correctness, and only one of those is recoverable.
    /// </remarks>
    public static bool IsRetryable(HttpRequestMessage? request)
    {
        if (request is null)
        {
            return false;
        }

        if (IdempotentMethods.Contains(request.Method.Method))
        {
            return true;
        }

        return request.Headers.TryGetValues(IdempotencyKeyHeader, out var values)
            && values.Any(value => !string.IsNullOrWhiteSpace(value));
    }
}
