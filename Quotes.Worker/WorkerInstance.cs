namespace Quotes.Worker;

/// <summary>
/// Identifies this process among the competing consumers.
/// </summary>
/// <remarks>
/// Injected rather than read from the environment wherever it is needed, so
/// tests can name an instance without setting process-wide state, and so two
/// instances started from the same folder cannot silently share an identity.
/// </remarks>
public sealed record WorkerInstance(string Id);
