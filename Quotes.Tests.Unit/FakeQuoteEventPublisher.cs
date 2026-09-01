using Quotes.Messaging.Publishing;

namespace Quotes.Tests.Unit;

/// <summary>
/// A hand-written <see cref="IQuoteEventPublisher"/> double that can model
/// exactly where a publish attempt failed.
/// </summary>
/// <remarks>
/// Not NSubstitute, on purpose. The crash scenario Day 20 has to prove -
/// "the process died between the message reaching the broker and the outbox
/// row being marked sent" - needs a double that records the call and THEN
/// throws, so a test can tell "the broker never saw this" apart from "the
/// broker saw this, something after that failed". A mock's Throws() setup
/// makes the call never happen at all; it cannot represent a failure that
/// occurs after the effect it is guarding.
/// </remarks>
public sealed class FakeQuoteEventPublisher : IQuoteEventPublisher
{
    public List<(string EventType, string MessageId)> Sent { get; } = new();

    /// <summary>
    /// When set, PublishAsync throws this after recording the call - models a
    /// send that reached the broker, followed by the caller (the relay) never
    /// getting to act on that success.
    /// </summary>
    public Exception? ThrowAfterRecording { get; set; }

    public Task PublishAsync<T>(T payload, string eventType, string messageId, CancellationToken cancellationToken = default)
    {
        Sent.Add((eventType, messageId));

        if (ThrowAfterRecording is not null)
        {
            throw ThrowAfterRecording;
        }

        return Task.CompletedTask;
    }
}
