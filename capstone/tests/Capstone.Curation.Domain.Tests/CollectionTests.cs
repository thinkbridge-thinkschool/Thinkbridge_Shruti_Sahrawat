using Capstone.Curation.Domain;
using Capstone.Curation.Domain.Events;
using Capstone.SharedKernel;
using FluentAssertions;

namespace Capstone.Curation.Domain.Tests;

/// <summary>
/// The aggregate's rules, asserted directly. No host, no database, no mocks -
/// a domain that needs any of those to be tested has a dependency it should not
/// have, so the absence of setup here is itself part of what is being checked.
/// </summary>
public class CollectionTests
{
    private static readonly CuratorId Curator = new("curator-1");
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static Collection ADraftWith(int itemCount)
    {
        var collection = Collection.Start(Curator, "Systems thinking");

        for (var i = 1; i <= itemCount; i++)
        {
            collection.AddItem(new QuoteId(i), Now);
        }

        return collection;
    }

    [Fact]
    public void ANewCollection_HasAnIdentityBeforeItIsEverSaved()
    {
        var collection = Collection.Start(Curator, "Systems thinking");

        collection.Id.Value.Should().NotBe(Guid.Empty,
            "an aggregate that has no identity until the database assigns one cannot be "
            + "referenced by anything written in the same transaction - which is the exact "
            + "reason Day 20's outbox write needed two SaveChanges calls");
        collection.Status.Should().Be(CollectionStatus.Draft);
        collection.PublishedAt.Should().BeNull();
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("")]
    [InlineData("   ")]
    public void ANameShorterThanTheMinimum_IsRejected(string name)
    {
        var start = () => Collection.Start(Curator, name);

        start.Should().Throw<DomainException>();
    }

    [Fact]
    public void ANameLongerThanTheMaximum_IsRejected()
    {
        var start = () => Collection.Start(Curator, new string('x', Collection.MaxNameLength + 1));

        start.Should().Throw<DomainException>();
    }

    [Fact]
    public void ANameIsTrimmed_AndMeasuredAfterTrimming()
    {
        var collection = Collection.Start(Curator, "   Systems thinking   ");

        collection.Name.Should().Be("Systems thinking");
    }

    [Fact]
    public void AnEmptyCuratorId_IsRejected()
    {
        var create = () => new CuratorId("  ");

        create.Should().Throw<DomainException>();
    }

    [Fact]
    public void TheSameQuote_CannotBeAddedTwice()
    {
        var collection = ADraftWith(1);

        var addAgain = () => collection.AddItem(new QuoteId(1), Now);

        addAgain.Should().Throw<DomainException>();
        collection.Items.Should().HaveCount(1);
    }

    [Fact]
    public void TheItemLimit_IsEnforcedAtTheBoundary()
    {
        var collection = ADraftWith(Collection.MaxItems);

        collection.Items.Should().HaveCount(Collection.MaxItems);

        var oneTooMany = () => collection.AddItem(new QuoteId(Collection.MaxItems + 1), Now);

        oneTooMany.Should().Throw<DomainException>();
    }

    [Fact]
    public void RemovingAQuoteThatIsNotThere_IsAnError_NotASilentNoOp()
    {
        var collection = ADraftWith(1);

        var remove = () => collection.RemoveItem(new QuoteId(99));

        remove.Should().Throw<DomainException>();
    }

    [Fact]
    public void AnEmptyCollection_CannotBePublished()
    {
        var collection = Collection.Start(Curator, "Systems thinking");

        var publish = () => collection.Publish(Now);

        publish.Should().Throw<DomainException>();
        collection.Status.Should().Be(CollectionStatus.Draft);
    }

    [Fact]
    public void Publishing_RecordsTheFactForSubscribers()
    {
        var collection = ADraftWith(2);

        collection.Publish(Now);

        collection.Status.Should().Be(CollectionStatus.Published);
        collection.PublishedAt.Should().Be(Now);

        collection.DomainEvents.Should().HaveCount(1);
        var published = collection.DomainEvents[0].Should().BeOfType<CollectionPublished>().Which;

        published.CollectionId.Should().Be(collection.Id);
        published.CuratorId.Should().Be(Curator);
        published.Name.Should().Be("Systems thinking");
        published.QuoteIds.Should().Equal(new QuoteId(1), new QuoteId(2));
        published.OccurredAt.Should().Be(Now);
    }

    [Fact]
    public void PublishingTwice_IsAnError_BecauseASilentSuccessWouldHideALostUpdate()
    {
        var collection = ADraftWith(1);
        collection.Publish(Now);

        var again = () => collection.Publish(Now);

        again.Should().Throw<DomainException>();
        collection.DomainEvents.Should().HaveCount(1, "a second publish must not fan out again");
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("rename")]
    public void APublishedCollection_IsFrozenUntilItIsUnpublished(string change)
    {
        var collection = ADraftWith(1);
        collection.Publish(Now);

        Action mutate;

        if (change == "add")
        {
            mutate = () => collection.AddItem(new QuoteId(2), Now);
        }
        else if (change == "remove")
        {
            mutate = () => collection.RemoveItem(new QuoteId(1));
        }
        else
        {
            mutate = () => collection.Rename("A different name");
        }

        mutate.Should().Throw<DomainException>(
            "followers have already seen this; changing it underneath them without a state "
            + "transition would make the feed and the collection silently disagree");
    }

    [Fact]
    public void Unpublishing_MakesItEditableAgain()
    {
        var collection = ADraftWith(1);
        collection.Publish(Now);

        collection.Unpublish();

        collection.Status.Should().Be(CollectionStatus.Draft);
        collection.PublishedAt.Should().BeNull();

        var addAfterUnpublish = () => collection.AddItem(new QuoteId(2), Now);
        addAfterUnpublish.Should().NotThrow();
    }

    [Fact]
    public void UnpublishingADraft_IsAnError()
    {
        var collection = ADraftWith(1);

        var unpublish = () => collection.Unpublish();

        unpublish.Should().Throw<DomainException>();
    }

    [Fact]
    public void ClearingEvents_StopsARepublishOnTheNextCommit()
    {
        var collection = ADraftWith(1);
        collection.Publish(Now);

        collection.ClearDomainEvents();

        collection.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void AQuoteIdMustBePositive()
    {
        var create = () => new QuoteId(0);

        create.Should().Throw<DomainException>();
    }
}
