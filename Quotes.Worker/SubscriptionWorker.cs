using Azure.Messaging.ServiceBus;
using Quotes.Messaging;
using Quotes.Messaging.Consuming;

namespace Quotes.Worker;

/// <summary>
/// Hosts one processor per subscription for the lifetime of the process.
/// </summary>
/// <remarks>
/// Both subscriptions are consumed by the same process here, which is a
/// convenience for the exercise rather than a recommendation - in production
/// the search indexer and the auditor would be separate deployables, scaled
/// independently, so a slow indexer could not delay the audit trail. What is
/// not a convenience is that they are separate <em>subscriptions</em>: each one
/// keeps its own cursor, its own delivery counts and its own dead-letter queue,
/// so a message the indexer dead-letters is still delivered normally to the
/// auditor.
/// </remarks>
public sealed class SubscriptionWorker : BackgroundService
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSettings _settings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SubscriptionWorker> _logger;
    private readonly WorkerInstance _instance;

    private SubscriptionProcessor? _searchIndexer;
    private SubscriptionProcessor? _auditLog;

    public SubscriptionWorker(
        ServiceBusClient client,
        ServiceBusSettings settings,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        ILogger<SubscriptionWorker> logger,
        WorkerInstance instance)
    {
        _client = client;
        _settings = settings;
        _scopeFactory = scopeFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _instance = instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Worker instance {InstanceId} starting: topic={Topic} subscriptions=[{Search}, {Audit}] maxConcurrentCalls={Concurrency}",
            _instance.Id, _settings.TopicName, _settings.SearchIndexerSubscription,
            _settings.AuditLogSubscription, _settings.MaxConcurrentCalls);

        _searchIndexer = new SubscriptionProcessor(
            _client, _settings.TopicName, _settings.SearchIndexerSubscription,
            new SearchIndexHandler(), _scopeFactory, _loggerFactory,
            _instance.Id, _settings.MaxConcurrentCalls);

        _auditLog = new SubscriptionProcessor(
            _client, _settings.TopicName, _settings.AuditLogSubscription,
            new AuditLogHandler(), _scopeFactory, _loggerFactory,
            _instance.Id, _settings.MaxConcurrentCalls);

        await _searchIndexer.StartAsync(stoppingToken);
        await _auditLog.StartAsync(stoppingToken);

        _logger.LogInformation("Worker instance {InstanceId} is consuming.", _instance.Id);

        try
        {
            // The processors run on their own threads; this task exists only to
            // hold the service alive until shutdown is requested.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown - not an error, and not something to log as
            // one. StopAsync does the actual draining.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker instance {InstanceId} stopping - draining in-flight messages.", _instance.Id);

        // StopProcessingAsync waits for handlers already running to finish
        // settling their messages. Skipping it would drop the locks instead:
        // every in-flight message would sit unsettled until its lock expired
        // and then be redelivered with its delivery count bumped, so a routine
        // restart would push messages towards the dead-letter queue for no
        // reason at all.
        if (_searchIndexer is not null) await _searchIndexer.StopAsync(cancellationToken);
        if (_auditLog is not null) await _auditLog.StopAsync(cancellationToken);

        await base.StopAsync(cancellationToken);

        if (_searchIndexer is not null) await _searchIndexer.DisposeAsync();
        if (_auditLog is not null) await _auditLog.DisposeAsync();

        _logger.LogInformation("Worker instance {InstanceId} stopped cleanly.", _instance.Id);
    }
}
