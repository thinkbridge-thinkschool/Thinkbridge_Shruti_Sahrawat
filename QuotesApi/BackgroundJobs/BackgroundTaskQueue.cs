using System.Threading.Channels;

namespace QuotesApi.BackgroundJobs;

// Bounded, not unbounded. An unbounded channel lets a slow consumer fall
// arbitrarily far behind a fast producer - the queue just grows until the
// process runs out of memory, silently, with no signal to the caller that
// anything is wrong. Bounded with FullMode.Wait means
// QueueBackgroundWorkItemAsync itself starts waiting once the queue is full,
// applying real backpressure to whoever is enqueueing instead of accepting
// unlimited work with no way to drain it.
public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
{
    private readonly Channel<Func<CancellationToken, ValueTask>> _queue;

    public BackgroundTaskQueue(int capacity)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
        };
        _queue = Channel.CreateBounded<Func<CancellationToken, ValueTask>>(options);
    }

    public async ValueTask QueueBackgroundWorkItemAsync(Func<CancellationToken, ValueTask> workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        await _queue.Writer.WriteAsync(workItem);
    }

    public async ValueTask<Func<CancellationToken, ValueTask>> DequeueAsync(CancellationToken cancellationToken)
    {
        return await _queue.Reader.ReadAsync(cancellationToken);
    }
}
