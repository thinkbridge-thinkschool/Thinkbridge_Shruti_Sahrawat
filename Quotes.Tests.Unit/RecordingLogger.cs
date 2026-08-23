using Microsoft.Extensions.Logging;

namespace Quotes.Tests.Unit;

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that keeps what it was told.
/// </summary>
/// <remarks>
/// Deliberately not an NSubstitute mock. <c>LogError(ex, "...", args)</c> is an
/// extension method that funnels into
/// <c>Log&lt;FormattedLogValues&gt;(...)</c>, so a substitute has to be asserted
/// against a closed generic over an internal type — the assertion ends up
/// matching the plumbing rather than the message, and breaks whenever the
/// logging internals change.
///
/// Recording the formatted message instead means a test can assert what was
/// actually written, which is the thing that matters when the log line is the
/// only surviving record of a failure.
/// </remarks>
public sealed class RecordingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

    private readonly List<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Add(new Entry(logLevel, formatter(state, exception), exception));
    }
}
