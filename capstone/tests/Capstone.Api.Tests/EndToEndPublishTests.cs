using System.Diagnostics;
using System.Net;
using FluentAssertions;

namespace Capstone.Api.Tests;

/// <summary>
/// One test, the whole slice: a curator publishes and a follower sees it.
/// </summary>
/// <remarks>
/// <b>Why there is exactly one.</b> This is the only test in the repository
/// that asserts across every boundary at once - HTTP, the aggregate, one
/// transaction, a table, a background thread, a subscriber in another module,
/// and back out through HTTP. That is what makes it worth having and also what
/// makes it the most expensive test here to own: it is the slowest, and it is
/// the only one whose failure does not tell you where the problem is. A suite
/// of these is a suite that takes ten minutes to say something went wrong
/// somewhere. So: one, guarding the claim the whole design exists to make, and
/// everything else pushed down to the layer that can answer precisely.
///
/// <b>It waits, and the waiting is the assertion.</b> Publish returns once the
/// collection and the outbox row are committed, deliberately before the
/// fan-out. So "the feed contains it" is not true at any instant this test
/// controls - it becomes true. Polling with a deadline is the honest shape for
/// that, and the alternatives are both worse: a fixed sleep is either flaky or
/// slow and usually manages both, and draining the relay by hand from the test
/// would be asserting against a delivery path that no longer resembles the one
/// running in production.
///
/// <b>What it would catch that nothing else does.</b> Every seam between the
/// pieces. A relay registered as a singleton holding a scoped DbContext. A
/// hosted service that never starts. An integration event whose JSON does not
/// round-trip. A subscriber wired to the wrong follower directory instance. All
/// four are wiring mistakes, which is precisely the category a unit test cannot
/// see and a composition root is made of.
/// </remarks>
public sealed class EndToEndPublishTests
{
    /// <summary>
    /// Generous on purpose. The relay polls every 100ms in this test, so a
    /// healthy run finishes in well under a second; ten seconds is there so
    /// that a slow CI runner reports a real failure rather than a timeout.
    /// </summary>
    private static readonly TimeSpan FanOutDeadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_published_collection_reaches_a_followers_feed()
    {
        using var app = new CapstoneApiFactory(relayPollIntervalMilliseconds: 100);
        using var client = app.CreateClient();

        await client.FollowAsync("alice", "bob");
        await client.FollowAsync("alice", "carol");

        var id = await client.StartCollectionAsync("alice", "Distributed Systems Wisdom");

        (await client.AddItemAsync(id, 1)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.AddItemAsync(id, 2)).StatusCode.Should().Be(HttpStatusCode.OK);

        var published = await client.PublishAsync(id, "alice");

        published.StatusCode.Should().Be(HttpStatusCode.OK);

        // The response says nothing about delivery, and that is the design
        // rather than an omission - Day 30 removed the messagesRelayed count
        // precisely so that a curator's publish could not fail because
        // somebody else's feed store was down.
        (await published.Content.ReadAsStringAsync()).Should().NotContain("elivered");

        var bobsFeed = await Eventually(
            () => client.FeedAsync("bob"),
            feed => feed.Count == 1,
            "bob's feed to receive the published collection");

        bobsFeed.Single().Name.Should().Be("Distributed Systems Wisdom");
        bobsFeed.Single().CollectionId.Should().Be(id);
        bobsFeed.Single().CuratorId.Should().Be("alice");

        // Fan-out on write means every follower gets their own entry. One
        // follower receiving it is not evidence the loop runs.
        var carolsFeed = await Eventually(
            () => client.FeedAsync("carol"),
            feed => feed.Count == 1,
            "carol's feed to receive the same collection");

        carolsFeed.Single().CollectionId.Should().Be(id);

        // And the row is acknowledged, not deleted. This is the half of Day
        // 30's design that the feed alone cannot show: an outbox that dequeued
        // on delivery would produce exactly the same two feeds and leave no
        // record that the message was ever handled.
        var outbox = await Eventually(
            () => client.OutboxAsync(),
            rows => rows.Count == 1 && rows[0].Delivered,
            "the outbox row to be marked sent");

        outbox[0].SentAt.Should().NotBeNull();
        outbox[0].SentAt.Should().BeOnOrAfter(outbox[0].OccurredAt,
            "a row cannot be acknowledged before it was staged");
    }

    /// <summary>
    /// Polls until the condition holds or the deadline passes, then returns the
    /// last value it saw so the assertion that follows can report it.
    /// </summary>
    /// <remarks>
    /// Returns rather than throws on timeout, deliberately. A helper that threw
    /// "timed out waiting" would replace the real assertion message - "expected
    /// 1 item, found 0" - with one that says only that waiting did not help.
    /// </remarks>
    private static async Task<T> Eventually<T>(
        Func<Task<T>> read, Func<T, bool> until, string what)
    {
        var clock = Stopwatch.StartNew();
        var latest = await read();

        while (!until(latest) && clock.Elapsed < FanOutDeadline)
        {
            await Task.Delay(25);
            latest = await read();
        }

        if (!until(latest))
        {
            // Not a failure on its own - the caller's assertion decides that -
            // but worth leaving in the output, because "it did eventually
            // arrive, at 9.6 seconds" and "it never arrived" are different
            // problems and the assertion message cannot tell them apart.
            Console.WriteLine(
                $"Waited {clock.Elapsed.TotalSeconds:F1}s for {what} and the condition never held.");
        }

        return latest;
    }
}
