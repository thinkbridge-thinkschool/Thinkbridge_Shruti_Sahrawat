namespace Quotes.Messaging.Consuming;

/// <summary>
/// A received message, stripped of every Service Bus type.
/// </summary>
/// <remarks>
/// The dispatcher and the handlers are written against this record rather than
/// against <c>ServiceBusReceivedMessage</c>, which is sealed, has an internal
/// constructor, and can only really be produced by a live broker. Depending on
/// it directly would mean the idempotency and poison-classification logic -
/// the part of this exercise that actually has interesting behaviour - could
/// only be tested by standing up a container. This record is the seam that
/// lets those rules be tested as plain in-memory code.
/// </remarks>
public sealed record IncomingMessage(
    string MessageId,
    string EventType,
    string Body,
    int DeliveryCount);
