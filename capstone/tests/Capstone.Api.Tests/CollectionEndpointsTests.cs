using System.Net;
using FluentAssertions;

namespace Capstone.Api.Tests;

/// <summary>
/// The API layer: routing, model binding, status codes, and the mapping from a
/// broken invariant to a response a caller can act on.
/// </summary>
/// <remarks>
/// <b>What this layer is for, given the two below it.</b> Every rule asserted
/// here is already asserted in Capstone.Curation.Domain.Tests, and asserting it
/// twice would be waste if these tests were about the rules. They are not. They
/// are about the translation: that "a published collection cannot be added to"
/// arrives at an HTTP caller as a 400 carrying the domain's own sentence, that a
/// collection nobody owns is a 404 while a collection somebody else owns is
/// deliberately not, and that the record written by one request is found by the
/// next one. None of those are visible from inside the domain, and all of them
/// have been broken by a one-line change to a middleware before.
///
/// The word "polish" in this day's brief is worth pinning down here. Nothing in
/// this file adds a feature. What it adds is the ability to notice that a
/// feature stopped working, at the layer a user meets it.
/// </remarks>
public sealed class CollectionEndpointsTests
{
    private const string EventTypeOnTheWire = "curation.collection.published.v1";

    [Fact]
    public async Task Starting_a_collection_returns_an_id_later_requests_can_use()
    {
        using var app = new CapstoneApiFactory();
        using var client = app.CreateClient();

        var id = await client.StartCollectionAsync("alice", "Distributed Systems Wisdom");

        id.Should().NotBe(Guid.Empty, "the domain mints a Guid v7 before the row is ever written");

        var added = await client.AddItemAsync(id, 1);

        added.StatusCode.Should().Be(HttpStatusCode.OK,
            "an id the API just handed out has to address the collection it created");
    }

    [Fact]
    public async Task A_collection_outlives_the_request_that_created_it()
    {
        using var app = new CapstoneApiFactory();
        using var client = app.CreateClient();

        var id = await client.StartCollectionAsync("alice", "Distributed Systems Wisdom");

        // Three separate requests, three separate DbContexts, three separate
        // scopes. This is the claim Day 29 made when the repository stopped
        // being a dictionary, and it is only observable from out here: an
        // in-memory store behind a scoped service would have passed every
        // domain test in the suite and failed this.
        (await client.AddItemAsync(id, 1)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.AddItemAsync(id, 2)).StatusCode.Should().Be(HttpStatusCode.OK);

        var third = await client.AddItemAsync(id, 3);

        third.StatusCode.Should().Be(HttpStatusCode.OK);
        (await third.Content.ReadAsStringAsync()).Should().Contain("3",
            "the third request sees the two items the first two wrote");
    }

    [Fact]
    public async Task Adding_an_item_to_a_collection_that_does_not_exist_is_404()
    {
        using var app = new CapstoneApiFactory();
        using var client = app.CreateClient();

        var response = await client.AddItemAsync(Guid.NewGuid(), 1);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Publishing_a_collection_that_does_not_exist_is_a_400_not_a_404()
    {
        using var app = new CapstoneApiFactory();
        using var client = app.CreateClient();

        var response = await client.PublishAsync(Guid.NewGuid(), "alice");

        // Pinned deliberately, because it is an inconsistency rather than a
        // decision. The items endpoint checks for null itself and answers 404;
        // publish delegates to PublishCollectionHandler, which raises a
        // DomainException, which the middleware maps to 400. Two endpoints, the
        // same missing collection, two different status codes.
        //
        // Left as it is for today rather than quietly corrected, because the
        // fix is not obviously "make publish 404": the handler answers "not
        // yours" with the same message on purpose, and a 404 there would make
        // the two cases distinguishable again by status code - which is the
        // information disclosure the shared message exists to prevent. Written
        // down here so that the next person to touch it is choosing rather than
        // discovering.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ErrorMessageAsync()).Should().Contain("was not found");
    }

    [Fact]
    public async Task Publishing_a_collection_owned_by_someone_else_is_answered_as_if_it_did_not_exist()
    {
        using var app = new CapstoneApiFactory();
        using var client = app.CreateClient();

        var real = await client.StartCollectionAsync("alice", "Distributed Systems Wisdom");
        await client.AddItemAsync(real, 1);

        var imaginary = Guid.NewGuid();

        var somebodyElses = await client.PublishAsync(real, "mallory");
        var nothingAtAll = await client.PublishAsync(imaginary, "mallory");

        // The security property, asserted rather than commented: a caller who
        // does not own a collection must not be able to tell whether it exists.
        // If these two responses ever diverge, an id somebody guessed becomes
        // an id they know is real.
        //
        // Stated as "identical once the echoed id is removed", which is the
        // actual property. The first version of this test asserted the two
        // messages were byte-identical and failed, because each one carries the
        // id the caller itself supplied - and the fix that would have made that
        // assertion pass is deleting the id from the message, which helps
        // nobody and hides the request that failed. A test can be wrong in a
        // direction that damages the code, and this one was.
        var refusedToMallory = await somebodyElses.ErrorMessageAsync();
        var neverExisted = await nothingAtAll.ErrorMessageAsync();

        somebodyElses.StatusCode.Should().Be(nothingAtAll.StatusCode);

        Redact(refusedToMallory, real)
            .Should().Be(Redact(neverExisted, imaginary),
                "a collection you do not own must be indistinguishable from one that is not there");

        // And the echoed id is the caller's own, not anyone else's - the other
        // half of "no information beyond what you already sent".
        refusedToMallory.Should().Be($"Collection {real} was not found.");
    }

    private static string Redact(string message, Guid id) => message.Replace(id.ToString(), "{id}");

    [Fact]
    public async Task Publishing_stages_exactly_one_outbox_row_and_leaves_it_unsent()
    {
        using var app = new CapstoneApiFactory();
        using var client = app.CreateClient();

        var id = await client.StartCollectionAsync("alice", "Distributed Systems Wisdom");
        await client.AddItemAsync(id, 1);

        var published = await client.PublishAsync(id, "alice");

        published.StatusCode.Should().Be(HttpStatusCode.OK);

        var outbox = await client.OutboxAsync();

        outbox.Should().ContainSingle("publishing raises exactly one integration event");
        outbox[0].EventType.Should().Be(EventTypeOnTheWire,
            "the event type is a wire contract - a subscriber filters on this string, so renaming "
            + "the constant behind it is a breaking change and has to fail here rather than compile");

        // The row exists and nobody has taken it. This is the state Day 30's
        // walkthrough could only catch by accident, because the relay took it
        // within 250ms; the test host parks the relay so the state can be
        // asserted instead of raced for.
        outbox[0].Delivered.Should().BeFalse();
        outbox[0].SentAt.Should().BeNull();
    }

    [Fact]
    public async Task Publishing_an_empty_collection_is_rejected_with_the_domain_message()
    {
        using var app = new CapstoneApiFactory();
        using var client = app.CreateClient();

        var id = await client.StartCollectionAsync("alice", "Empty");

        var response = await client.PublishAsync(id, "alice");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ErrorMessageAsync())
            .Should().Be("A collection needs at least one quote before it can be published.",
                "the middleware forwards the domain's sentence rather than inventing an API one");

        (await client.OutboxAsync()).Should().BeEmpty(
            "a rejected publish announces nothing - the invariant fails before the event is raised");
    }

    [Fact]
    public async Task Publishing_twice_is_rejected_and_announces_nothing_the_second_time()
    {
        using var app = new CapstoneApiFactory();
        using var client = app.CreateClient();

        var id = await client.StartCollectionAsync("alice", "Distributed Systems Wisdom");
        await client.AddItemAsync(id, 1);

        (await client.PublishAsync(id, "alice")).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.PublishAsync(id, "alice");

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The half that matters. A second publish being refused is the
        // aggregate's rule; a second publish leaving no second outbox row is
        // what stops a duplicate fan-out reaching every follower.
        (await client.OutboxAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_collection_holding_a_quote_the_catalog_does_not_have_cannot_be_published()
    {
        using var app = new CapstoneApiFactory();
        using var client = app.CreateClient();

        var id = await client.StartCollectionAsync("alice", "Distributed Systems Wisdom");

        // 99 is not in the seeded catalog. The aggregate cannot know that - it
        // holds QuoteIds and never quote text, deliberately - so this is the
        // cross-module check in PublishCollectionHandler, exercised through the
        // composition root that wires the two modules together.
        (await client.AddItemAsync(id, 99)).StatusCode.Should().Be(HttpStatusCode.OK,
            "adding is Curation's business alone; existence is Catalog's, and is asked at publish");

        var response = await client.PublishAsync(id, "alice");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ErrorMessageAsync()).Should().Contain("99");
    }

    [Fact]
    public async Task An_item_cannot_be_added_to_a_published_collection()
    {
        using var app = new CapstoneApiFactory();
        using var client = app.CreateClient();

        var id = await client.StartCollectionAsync("alice", "Distributed Systems Wisdom");
        await client.AddItemAsync(id, 1);
        await client.PublishAsync(id, "alice");

        var response = await client.AddItemAsync(id, 2);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ErrorMessageAsync()).Should().Contain("Unpublish it first",
            "publishing freezes the collection so that what followers saw and what the curator "
            + "holds cannot silently diverge");
    }

    [Fact]
    public async Task A_name_the_domain_refuses_is_a_400_and_not_a_500()
    {
        using var app = new CapstoneApiFactory();
        using var client = app.CreateClient();

        var response = await client.PostAsync(
            "/api/collections",
            JsonContent("""{"curatorId":"alice","name":"no"}"""));

        // The distinction DomainException exists to make. Without the
        // middleware this is an unhandled exception and a 500, which tells a
        // caller the server is broken when the request was.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ErrorMessageAsync()).Should().Contain("between 3 and 80");
    }

    private static StringContent JsonContent(string body)
        => new(body, System.Text.Encoding.UTF8, "application/json");
}
