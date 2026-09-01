namespace Quotes.Messaging;

/// <summary>
/// Everything the publisher and the worker need to find the topic.
/// </summary>
public sealed class ServiceBusSettings
{
    public const string SectionName = "ServiceBus";

    /// <summary>
    /// Connection string. Against the local emulator this ends
    /// <c>UseDevelopmentEmulator=true</c>, which is what tells the client to
    /// skip TLS and talk plain AMQP to localhost:5672.
    /// </summary>
    /// <remarks>
    /// A connection string is the emulator's only option - it has no Entra ID
    /// to issue tokens against, so <c>DefaultAzureCredential</c> cannot work
    /// there. In real Azure this field would be left empty and
    /// <see cref="FullyQualifiedNamespace"/> set instead, so the client
    /// authenticates with the same user-assigned managed identity the API
    /// already uses for Azure SQL, and no shared access key exists to leak.
    /// That is the shape this repo would ship; the emulator is the reason the
    /// key-based path exists at all.
    /// </remarks>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// e.g. <c>sb-quotes.servicebus.windows.net</c>. When set, this wins over
    /// <see cref="ConnectionString"/> and the client authenticates with
    /// <c>DefaultAzureCredential</c> instead of a key.
    /// </summary>
    public string? FullyQualifiedNamespace { get; set; }

    public string TopicName { get; set; } = "quote-events";

    /// <summary>
    /// The subscription the competing-consumer workers share. Every worker
    /// instance reads this same subscription, so each message goes to exactly
    /// one of them.
    /// </summary>
    public string SearchIndexerSubscription { get; set; } = "search-indexer";

    /// <summary>
    /// A second, independent subscription on the same topic. It receives its
    /// own copy of every message - this is fan-out, not competition.
    /// </summary>
    public string AuditLogSubscription { get; set; } = "audit-log";

    /// <summary>
    /// How many messages one worker instance handles at once. This is
    /// concurrency *within* an instance; running more instances is concurrency
    /// *across* them. Both reduce backlog, only the second survives the
    /// process dying.
    /// </summary>
    public int MaxConcurrentCalls { get; set; } = 4;
}
