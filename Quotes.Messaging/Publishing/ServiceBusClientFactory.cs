using Azure.Identity;
using Azure.Messaging.ServiceBus;

namespace Quotes.Messaging.Publishing;

/// <summary>
/// Creates the one <see cref="ServiceBusClient"/> everything else shares.
/// </summary>
/// <remarks>
/// One client per process, not one per send. The client owns the AMQP
/// connection and its links; creating one per message pays a TCP and TLS
/// handshake every time and leaks connections until the namespace starts
/// refusing them.
/// </remarks>
public static class ServiceBusClientFactory
{
    public static ServiceBusClient Create(ServiceBusSettings settings)
    {
        // Namespace wins when both are set, because it is the credential-based
        // path: no key exists to be stolen, rotated or accidentally committed.
        if (!string.IsNullOrWhiteSpace(settings.FullyQualifiedNamespace))
        {
            return new ServiceBusClient(settings.FullyQualifiedNamespace, new DefaultAzureCredential());
        }

        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            throw new InvalidOperationException(
                "Configure either ServiceBus:FullyQualifiedNamespace (managed identity) " +
                "or ServiceBus:ConnectionString (local emulator).");
        }

        return new ServiceBusClient(settings.ConnectionString);
    }
}
