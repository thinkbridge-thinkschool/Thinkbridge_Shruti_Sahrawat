namespace Capstone.SharedKernel;

/// <summary>
/// A business rule was broken.
/// </summary>
/// <remarks>
/// A distinct type, rather than the InvalidOperationException the current
/// QuotesApi aggregate throws, because the two mean different things to the
/// layer that catches them. A broken invariant is something the caller can fix
/// by sending different input, and belongs in a 400 with a message the user
/// can act on. An InvalidOperationException is usually a programming mistake
/// and belongs in a 500 and an alert. Sharing one type for both forces the API
/// layer to guess, and it will guess wrong in whichever direction is worse.
/// </remarks>
public sealed class DomainException : Exception
{
    public DomainException(string message) : base(message)
    {
    }
}
