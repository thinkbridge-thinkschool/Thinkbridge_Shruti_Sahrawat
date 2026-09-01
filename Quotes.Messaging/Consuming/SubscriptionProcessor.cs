using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quotes.Messaging.Data;

namespace Quotes.Messaging.Consuming;

/// <summary>
/// Binds one subscription to one handler and decides how every message is
/// settled.
/// </summary>
/// <remarks>
/// <para><b>Peek-lock, with auto-complete off.</b> The alternative,
/// ReceiveAndDelete, removes the message the instant it is handed over - if the
/// process dies a microsecond later the message is simply gone, with no retry
/// and no dead-letter. Peek-lock keeps it until it is explicitly settled, which
/// is what makes both redelivery and the dead-letter queue possible at all.
/// AutoCompleteMessages is off for the same reason: settlement is a decision
/// this code makes per outcome, not something that happens by default when the
/// handler returns.</para>
///
/// <para><b>Competing consumers.</b> Every instance of the worker creates this
/// same processor against the same subscription. The broker hands each message
/// to exactly one of them, so adding instances adds throughput and none of them
/// has to know how many others exist. That is distinct from the fan-out across
/// <em>subscriptions</em>: two subscriptions each get a copy of every message;
/// two instances on one subscription split the messages between them.</para>
/// </remarks>
public sealed class SubscriptionProcessor : IAsyncDisposable
{
    private readonly ServiceBusProcessor _processor;
    private readonly IQuoteEventHandler _handler;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly string _instanceId;

    public SubscriptionProcessor(
        ServiceBusClient client,
        string topicName,
        string subscriptionName,
        IQuoteEventHandler handler,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        string instanceId,
        int maxConcurrentCalls)
    {
        _handler = handler;
        _scopeFactory = scopeFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger($"Processor:{subscriptionName}");
        _instanceId = instanceId;

        _processor = client.CreateProcessor(topicName, subscriptionName, new ServiceBusProcessorOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            AutoCompleteMessages = false,
            MaxConcurrentCalls = maxConcurrentCalls,
        });

        _processor.ProcessMessageAsync += OnMessageAsync;
        _processor.ProcessErrorAsync += OnErrorAsync;
    }

    public Task StartAsync(CancellationToken cancellationToken) => _processor.StartProcessingAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => _processor.StopProcessingAsync(cancellationToken);

    private async Task OnMessageAsync(ProcessMessageEventArgs args)
    {
        var eventType = args.Message.Subject
                        ?? (args.Message.ApplicationProperties.TryGetValue("eventType", out var value) ? value?.ToString() : null)
                        ?? string.Empty;

        var incoming = new IncomingMessage(
            args.Message.MessageId,
            eventType,
            args.Message.Body.ToString(),
            args.Message.DeliveryCount);

        try
        {
            // A scope - and therefore a fresh DbContext - per message. A context
            // shared across messages would carry one message's tracked entities
            // into the next, and a failed SaveChanges leaves the context holding
            // entities that would be retried on the following message's write.
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

            var dispatcher = new QuoteEventDispatcher(
                db,
                _loggerFactory.CreateLogger<QuoteEventDispatcher>(),
                _instanceId);

            // A duplicate is a success, not a failure: the effect this message
            // asked for is already in place, so the right move is to settle it
            // and stop redelivering it.
            await dispatcher.DispatchAsync(incoming, _handler, args.CancellationToken);

            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (PoisonMessageException ex)
        {
            // Deterministically broken. Skip the retries entirely and put it
            // somewhere a human can find it, with the reason attached rather
            // than left to be reconstructed from logs.
            _logger.LogWarning(
                "Dead-lettering messageId={MessageId} immediately: {Reason}",
                incoming.MessageId, ex.Reason);

            await args.DeadLetterMessageAsync(
                args.Message,
                deadLetterReason: ex.Reason,
                deadLetterErrorDescription: ex.Description,
                cancellationToken: args.CancellationToken);
        }
        catch (Exception ex)
        {
            // Might be transient, so give it back and let it be redelivered.
            // If it is not transient after all, the delivery count climbs and
            // the broker dead-letters it at MaxDeliveryCount without this code
            // needing to count anything itself.
            _logger.LogError(
                ex,
                "Handler failed for messageId={MessageId} on delivery {DeliveryCount}; abandoning for redelivery",
                incoming.MessageId, incoming.DeliveryCount);

            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        }
    }

    private Task OnErrorAsync(ProcessErrorEventArgs args)
    {
        // Errors raised here are connection- and entity-level, not per-message -
        // the processor recovers on its own, so this logs rather than tears the
        // host down.
        _logger.LogError(args.Exception, "Service Bus processor error during {Operation}", args.ErrorSource);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _processor.DisposeAsync();
}
