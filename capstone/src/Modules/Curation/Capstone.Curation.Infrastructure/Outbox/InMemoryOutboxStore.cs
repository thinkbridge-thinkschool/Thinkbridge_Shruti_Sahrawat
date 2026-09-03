using System.Collections.Concurrent;

namespace Capstone.Curation.Infrastructure.Outbox;

/// <summary>
/// Scaffold outbox. The real one is a table, written in the publish
/// transaction and drained by a separate relay - Quotes.Outbox in the main
/// solution is a working implementation of exactly that.
/// </summary>
public sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly ConcurrentQueue<OutboxRecord> _pending = new();

    public void Enqueue(OutboxRecord record) => _pending.Enqueue(record);

    /// <summary>
    /// Takes everything currently staged. Stands in for the relay's
    /// "SELECT ... WHERE SentAt IS NULL" batch.
    /// </summary>
    public IReadOnlyList<OutboxRecord> DrainPending()
    {
        var drained = new List<OutboxRecord>();

        while (_pending.TryDequeue(out var record))
        {
            drained.Add(record);
        }

        return drained;
    }
}
