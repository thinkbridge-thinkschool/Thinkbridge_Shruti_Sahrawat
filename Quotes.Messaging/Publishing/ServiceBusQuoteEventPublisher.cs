using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace Quotes.Messaging.Publishing;

public sealed class ServiceBusQuoteEventPublisher : IQuoteEventPublisher, IAsyncDisposable
{
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusQuoteEventPublisher> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ServiceBusQuoteEventPublisher(
        ServiceBusClient client,
        ServiceBusSettings settings,
        ILogger<ServiceBusQuoteEventPublisher> logger)
    {
        _sender = client.CreateSender(settings.TopicName);
        _logger = logger;
    }

    public async Task PublishAsync<T>(T payload, string eventType, string messageId, CancellationToken cancellationToken = default)
    {
        var message = new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions))
        {
            MessageId = messageId,
            Subject = eventType,
            ContentType = "application/json",
        };

        // The subscription filters run inside the broker, against application
        // properties only - they cannot see the body. So the routing key has to
        // be promoted out of the payload and into a property, or every
        // subscriber receives everything and filters in code, which is exactly
        // the network traffic a topic filter exists to avoid.
        message.ApplicationProperties["eventType"] = eventType;

        await _sender.SendMessageAsync(message, cancellationToken);

        _logger.LogInformation(
            "Published {EventType} messageId={MessageId}", eventType, messageId);
    }

    public async ValueTask DisposeAsync() => await _sender.DisposeAsync();
}
