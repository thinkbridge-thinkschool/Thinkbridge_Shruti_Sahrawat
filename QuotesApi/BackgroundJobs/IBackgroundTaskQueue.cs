namespace QuotesApi.BackgroundJobs;

// The seam between "something happened that needs slow work done later" (an
// endpoint) and "the work actually running" (the hosted service). Neither
// side references the other directly - the endpoint enqueues and returns
// immediately; the hosted service dequeues on its own schedule. That
// decoupling is the whole point: the request thread is never blocked on the
// slow work, which is what this exercise is about.
public interface IBackgroundTaskQueue
{
    // Enqueues a unit of work. Returns as soon as it is queued, not when it
    // is run - queuing is O(1) and never touches the slow work itself.
    ValueTask QueueBackgroundWorkItemAsync(Func<CancellationToken, ValueTask> workItem);

    // Waits for the next item, honouring cancellationToken so a shutdown
    // request unblocks a dequeue that is waiting on an empty queue, rather
    // than hanging until something arrives that may never come.
    ValueTask<Func<CancellationToken, ValueTask>> DequeueAsync(CancellationToken cancellationToken);
}
