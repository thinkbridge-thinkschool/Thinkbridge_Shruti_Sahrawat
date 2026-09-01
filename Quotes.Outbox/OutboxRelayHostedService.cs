using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Quotes.Outbox;

/// <summary>
/// Runs <see cref="OutboxRelay"/> on a poll loop for the life of the process.
/// </summary>
/// <remarks>
/// A fresh scope - and therefore a fresh <see cref="OutboxDbContext"/> - every
/// poll, the same reason <c>SubscriptionProcessor</c> opens one per message on
/// the consumer side: a context that accumulated tracked rows across polls
/// would eventually retry stale state from an earlier iteration instead of
/// asking the database what is actually unsent right now.
///
/// Polls again immediately, with no delay, whenever the last batch published
/// anything - there may be more work waiting - and only backs off to
/// <see cref="_pollInterval"/> once a batch comes back empty. A relay that
/// always waited the full interval between polls would add that interval to
/// every message's latency even when the outbox is busy; a relay that never
/// backed off would spin a CPU core against an empty table between quotes.
/// </remarks>
public sealed class OutboxRelayHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxRelayHostedService> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly int _batchSize;

    public OutboxRelayHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxRelayHostedService> logger,
        TimeSpan pollInterval,
        int batchSize)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _pollInterval = pollInterval;
        _batchSize = batchSize;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Outbox relay starting: batch size {BatchSize}, poll interval {PollInterval}",
            _batchSize, _pollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            bool publishedAny;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
                var results = await relay.RelayBatchAsync(_batchSize, stoppingToken);
                publishedAny = results.Any(r => r.Outcome == OutboxRelayOutcome.Published);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failure here is something RelayBatchAsync did not already
                // turn into a per-row Failed result - the database itself is
                // unreachable, most likely. Logged and retried after the poll
                // interval rather than crashing the process: an outbox row
                // that cannot be reached yet is not lost, it is exactly where
                // it will be found again next poll.
                _logger.LogError(ex, "Outbox relay poll failed; retrying after the poll interval");
                publishedAny = false;
            }

            if (!publishedAny)
            {
                try
                {
                    await Task.Delay(_pollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
