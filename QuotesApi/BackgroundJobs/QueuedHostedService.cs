namespace QuotesApi.BackgroundJobs;

// A BackgroundService, not a raw IHostedService. IHostedService gives you
// StartAsync/StopAsync and nothing else - a long-running loop has to manage
// its own Task and its own "am I still supposed to be running" bookkeeping by
// hand. BackgroundService already wraps that: override ExecuteAsync with a
// single loop, and the base class starts it from StartAsync and awaits its
// completion from StopAsync, passing the host's shutdown token into the loop
// for you rather than making every implementation wire that up itself.
public sealed class QueuedHostedService : BackgroundService
{
    private readonly IBackgroundTaskQueue _taskQueue;
    private readonly ILogger<QueuedHostedService> _logger;

    public QueuedHostedService(IBackgroundTaskQueue taskQueue, ILogger<QueuedHostedService> logger)
    {
        _taskQueue = taskQueue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("QueuedHostedService starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            Func<CancellationToken, ValueTask> workItem;
            try
            {
                // Parks here, off the request thread entirely, until either a
                // work item arrives or stoppingToken is cancelled. This is the
                // graceful-shutdown path: on shutdown the host cancels
                // stoppingToken, DequeueAsync's wait throws
                // OperationCanceledException, and the loop exits on its own
                // rather than being killed mid-iteration.
                workItem = await _taskQueue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await workItem(stoppingToken);
            }
            catch (Exception ex)
            {
                // A failing work item must never take the whole loop down -
                // one bad item would otherwise silently stop every future
                // item from ever being processed, with nothing pointing at
                // why background jobs simply stopped running.
                _logger.LogError(ex, "Background work item threw and was not swallowed silently");
            }
        }

        _logger.LogInformation("QueuedHostedService stopping - drain loop exited on cancellation");
    }
}
