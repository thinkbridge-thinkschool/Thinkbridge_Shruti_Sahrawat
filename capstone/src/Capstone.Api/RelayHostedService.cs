using Capstone.Curation.Infrastructure.Outbox;

namespace Capstone.Api;

/// <summary>
/// Drains the outbox off the request path.
/// </summary>
/// <remarks>
/// Day 28's design review found the capstone's strongest claim contradicted by
/// its own composition root. The design says the commit is the boundary of the
/// promise: a curator's publish returns once the state change and the outbox row
/// are committed together, and everything after that is catch-up. The publish
/// endpoint did not do that. It drained the relay inline and returned the
/// delivered count in the response body, which meant a failure inside Sharing
/// surfaced to the curator as a failed publish for a collection that was already
/// published and committed, and publish latency was still a function of follower
/// count - the exact two properties the outbox exists to avoid.
///
/// A comment in Program.cs saying the inline drain was "the scaffold's shortcut,
/// not the design" did not stop it being the design, in the same way that a
/// folder does not stop one module reaching into another. Same lesson as
/// <c>ModuleBoundaries</c>, applied to a different boundary: if the shape
/// matters, make it structural.
///
/// The loop polls rather than being signalled by the writer, because the real
/// relay is a separate process reading a table it does not share memory with,
/// and has no way to be woken either. Keeping that property means the difference
/// between this and the Service Bus relay from Day 20 is where it reads from,
/// not how it is driven.
///
/// Known gap, scheduled rather than hidden: <see cref="InMemoryOutboxStore"/>
/// drains destructively, so a record whose handler throws is gone. The real
/// outbox marks SentAt only after the broker acknowledges, which is what makes
/// at-least-once true there and not yet here. Build plan, day 2.
/// </remarks>
internal sealed class RelayHostedService(
    InProcessRelay relay,
    ILogger<RelayHostedService> logger) : BackgroundService
{
    /// <summary>
    /// Short enough that the walkthrough in the README stays pleasant, long
    /// enough that the feed is observably eventually consistent rather than
    /// accidentally synchronous. A reader who queries the feed the instant
    /// publish returns and sees it empty is being shown the design, not a bug.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await relay.DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A relay that dies on one bad message stops delivering every
                // later one, which turns a single poison record into a total
                // outage of the fan-out. Log and stay alive. The real answer is
                // the dead-letter queue Day 19 built, once this reads a broker.
                logger.LogError(ex, "Outbox drain failed; the relay will retry on the next poll.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
