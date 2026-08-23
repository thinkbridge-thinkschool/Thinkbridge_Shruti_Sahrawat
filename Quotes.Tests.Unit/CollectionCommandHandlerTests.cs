using FluentAssertions;
using NSubstitute;
using QuotesApi.Domain;
using QuotesApi.Features.Collections;
using QuotesApi.Repositories;

namespace Quotes.Tests.Unit;

/// <summary>
/// The write side of the CQRS split: command handlers that go through the
/// aggregate.
/// </summary>
/// <remarks>
/// No database here on purpose. The question these tests ask is "does the
/// handler route the work through the aggregate and stop when the aggregate
/// objects" — a question about collaboration, not about persistence. A
/// substituted repository answers it in about a millisecond and, more usefully,
/// lets the test assert that nothing was written when an invariant failed, which
/// is awkward to observe once a real DbContext is involved.
/// </remarks>
public class CreateCollectionHandlerTests
{
    [Fact]
    public async Task Handle_ValidCommand_SavesACollectionBuiltThroughTheAggregate()
    {
        var repository = Substitute.For<ICollectionRepository>();
        var handler = new CreateCollectionHandler(repository);

        await handler.Handle(new CreateCollectionCommand("  My Collection  ", "owner-1"), CancellationToken.None);

        await repository.Received(1).AddAsync(
            Arg.Is<Collection>(c => c.Name == "My Collection" && c.OwnerId == "owner-1"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_NameFailsTheInvariant_ThrowsBeforeTouchingTheDatabase(string name)
    {
        var repository = Substitute.For<ICollectionRepository>();
        var handler = new CreateCollectionHandler(repository);

        var act = async () => await handler.Handle(
            new CreateCollectionCommand(name, "owner-1"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        // The point of putting the rule in the constructor rather than in the
        // handler: an invalid collection cannot reach persistence, because it
        // cannot be constructed at all.
        await repository.DidNotReceive().AddAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyOwnerId_ThrowsBeforeTouchingTheDatabase()
    {
        var repository = Substitute.For<ICollectionRepository>();
        var handler = new CreateCollectionHandler(repository);

        var act = async () => await handler.Handle(
            new CreateCollectionCommand("My Collection", "  "), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await repository.DidNotReceive().AddAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PassesTheCallersCancellationTokenToTheRepository()
    {
        var repository = Substitute.For<ICollectionRepository>();
        var handler = new CreateCollectionHandler(repository);
        using var cts = new CancellationTokenSource();

        await handler.Handle(new CreateCollectionCommand("My Collection", "owner-1"), cts.Token);

        await repository.Received(1).AddAsync(Arg.Any<Collection>(), cts.Token);
    }
}

public class AddQuoteToCollectionHandlerTests
{
    private static ICollectionRepository RepositoryReturning(Collection? collection, int forId = 1)
    {
        var repository = Substitute.For<ICollectionRepository>();
        repository.GetByIdAsync(forId, Arg.Any<CancellationToken>()).Returns(collection);
        return repository;
    }

    [Fact]
    public async Task Handle_UnknownCollection_ReturnsFalseAndWritesNothing()
    {
        var repository = RepositoryReturning(null, forId: 99);
        var handler = new AddQuoteToCollectionHandler(repository, new TestClock());

        var found = await handler.Handle(new AddQuoteToCollectionCommand(99, 42), CancellationToken.None);

        found.Should().BeFalse();
        await repository.DidNotReceive().UpdateAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_KnownCollection_AddsTheQuoteAndSaves()
    {
        var collection = new Collection("My Collection", "owner-1");
        var repository = RepositoryReturning(collection);
        var handler = new AddQuoteToCollectionHandler(repository, new TestClock());

        var found = await handler.Handle(new AddQuoteToCollectionCommand(1, 42), CancellationToken.None);

        found.Should().BeTrue();
        collection.Items.Should().ContainSingle(i => i.QuoteId == 42);
        await repository.Received(1).UpdateAsync(collection, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_StampsAddedAtFromTheInjectedClock()
    {
        var instant = new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var collection = new Collection("My Collection", "owner-1");
        var repository = RepositoryReturning(collection);
        var handler = new AddQuoteToCollectionHandler(repository, new TestClock(instant));

        await handler.Handle(new AddQuoteToCollectionCommand(1, 42), CancellationToken.None);

        // This is the assertion that would have failed before the clock was
        // threaded through: AddedAt used to come from DateTime.UtcNow inside the
        // value object, and no amount of DI registration changed it.
        collection.Items.Should().ContainSingle()
            .Which.AddedAt.Should().Be(instant.UtcDateTime);
    }

    [Fact]
    public async Task Handle_DuplicateQuoteId_LetsTheAggregateThrowAndDoesNotSave()
    {
        var clock = new TestClock();
        var collection = new Collection("My Collection", "owner-1");
        collection.AddItem(42, clock.UtcNow);
        var repository = RepositoryReturning(collection);
        var handler = new AddQuoteToCollectionHandler(repository, clock);

        var act = async () => await handler.Handle(
            new AddQuoteToCollectionCommand(1, 42), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        // The handler does not re-check the invariant, and it must not: the rule
        // lives in exactly one place. What it does owe the caller is not writing
        // a half-applied change.
        await repository.DidNotReceive().UpdateAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTheCollectionIsFull_LetsTheAggregateThrowAndDoesNotSave()
    {
        var clock = new TestClock();
        var collection = new Collection("My Collection", "owner-1");
        for (var i = 1; i <= Collection.MaxItems; i++)
        {
            collection.AddItem(i, clock.UtcNow);
        }

        var repository = RepositoryReturning(collection);
        var handler = new AddQuoteToCollectionHandler(repository, clock);

        var act = async () => await handler.Handle(
            new AddQuoteToCollectionCommand(1, Collection.MaxItems + 1), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await repository.DidNotReceive().UpdateAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NonPositiveQuoteId_LetsTheValueObjectThrow()
    {
        var collection = new Collection("My Collection", "owner-1");
        var repository = RepositoryReturning(collection);
        var handler = new AddQuoteToCollectionHandler(repository, new TestClock());

        var act = async () => await handler.Handle(
            new AddQuoteToCollectionCommand(1, 0), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        collection.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_PassesTheCallersCancellationTokenThroughBothRepositoryCalls()
    {
        var collection = new Collection("My Collection", "owner-1");
        var repository = RepositoryReturning(collection);
        var handler = new AddQuoteToCollectionHandler(repository, new TestClock());
        using var cts = new CancellationTokenSource();

        await handler.Handle(new AddQuoteToCollectionCommand(1, 42), cts.Token);

        // Day 2's rule, asserted rather than assumed: the token reaches every I/O
        // call, not just the first one. A token that stops at the read leaves the
        // write running after the client has gone.
        await repository.Received(1).GetByIdAsync(1, cts.Token);
        await repository.Received(1).UpdateAsync(collection, cts.Token);
    }
}
