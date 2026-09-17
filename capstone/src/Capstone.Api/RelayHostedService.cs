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
/// <b>A scope per poll, as of day 2.</b> The outbox is a table now, read
/// through a scoped <c>CurationDbContext</c>, and a hosted service is a
/// singleton. Injecting the relay directly would be a captive dependency - the
/// container would hand this singleton one DbContext to keep for the lifetime
/// of the process, which is both not thread-safe and a change tracker that
/// grows until the process restarts. Resolving inside a fresh scope each poll
/// is also the more faithful shape: the real relay opens a connection, reads a
/// batch, acknowledges it, and lets go.
/// </remarks>
internal sealed class RelayHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<RelayHostedService> logger) : BackgroundService
{
    /// <summary>
    /// Short enough that the walkthrough in the README stays pleasant, long
    /// enough that the feed is observably eventually consistent rather than
    /// accidentally synchronous. A reader who queries the feed the instant
    /// publish returns and sees it empty is being shown the design, not a bug.
    /// </summary>
    /// <remarks>
    /// Now that the outbox is a table this interval has a cost it did not have
    /// against a queue: it is a query every 250ms per instance, forever, mostly
    /// returning nothing. Day 26's dependency breakdown found exactly this
    /// pattern in the main solution - 366 SQLite calls in half an hour from a
    /// five-second poll - and it is the strongest argument for day 3 happening
    /// on schedule rather than being deferred, because a broker is pushed to
    /// rather than polled.
    /// </remarks>
    public const int DefaultPollIntervalMilliseconds = 250;

    /// <summary>
    /// <c>Relay:PollIntervalMilliseconds</c>, defaulting to
    /// <see cref="DefaultPollIntervalMilliseconds"/>.
    /// </summary>
    /// <remarks>
    /// Read from configuration as of day 31, and not only so the tests can
    /// change it. A poll interval is an operational dial: the right value
    /// depends on how much delivery lag a feed can tolerate against how much a
    /// mostly-empty query every quarter second costs, and both of those are
    /// properties of a deployment rather than of this code. Hard-coding it
    /// meant the only way to answer "what if we polled every two seconds in
    /// production" was a rebuild.
    ///
    /// What the tests get for free is the reason it is <i>noticed</i> here.
    /// Capstone.Api.Tests sets an hour, which parks the relay after its first
    /// drain so that an endpoint test can assert a row is still unsent without
    /// racing a background thread; the one end-to-end test sets 100ms so the
    /// fan-out it is actually about does not take a quarter of a second to
    /// start. Neither needed a flag that only exists under test.
    ///
    /// Bound at construction rather than re-read each pass. A value that could
    /// change mid-loop reads as a feature and is really just a timing question
    /// nobody has asked - and this service is restarted by every deploy anyway.
    /// </remarks>
    private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(
        Math.Max(1, configuration.GetValue("Relay:PollIntervalMilliseconds", DefaultPollIntervalMilliseconds)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Outbox relay started; polling every {PollIntervalMs}ms.", _pollInterval.TotalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();

                var relay = scope.ServiceProvider.GetRequiredService<InProcessRelay>();

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
                // outage of the fan-out. Log and stay alive.
                //
                // As of day 2 this is a retry rather than a loss: the row that
                // failed is still unsent, so the next poll attempts it again.
                // Which surfaces the next gap, named here rather than
                // discovered later - a row that can never succeed is now
                // retried forever at four attempts a second, and nothing
                // counts attempts or gives up. The answer is a dead-letter
                // path, which is what Day 19 built and what day 3 inherits.
                logger.LogError(ex, "Outbox drain failed; the unsent rows will be retried on the next poll.");
            }

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
