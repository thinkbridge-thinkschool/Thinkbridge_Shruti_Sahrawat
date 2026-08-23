extern alias QuotesApiProject;

using System;
using System.Linq;
using QuotesApiProject::QuotesApi.Domain;
using Xunit;

public class CollectionDomainTests
{
    private static readonly DateTimeOffset At = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void EmptyOrInvalidName_ThrowsException()
    {
        Assert.Throws<InvalidOperationException>(() => new Collection("Hi", "owner-1"));
        Assert.Throws<InvalidOperationException>(() => new Collection(new string('a', 81), "owner-1"));
    }

    [Fact]
    public void AddingBeyond50Items_ThrowsException()
    {
        var collection = new Collection("My Collection", "owner-1");
        for (int i = 1; i <= 50; i++)
        {
            collection.AddItem(i, At);
        }

        Assert.Throws<InvalidOperationException>(() => collection.AddItem(51, At));
    }

    [Fact]
    public void DuplicateQuoteId_ThrowsException()
    {
        var collection = new Collection("My Collection", "owner-1");
        collection.AddItem(100, At);

        Assert.Throws<InvalidOperationException>(() => collection.AddItem(100, At));
    }

    [Fact]
    public void RemovingNonExistentItem_ThrowsException()
    {
        var collection = new Collection("My Collection", "owner-1");

        Assert.Throws<InvalidOperationException>(() => collection.RemoveItem(999));
    }

    [Fact]
    public void AddingThenRemovingItem_LeavesZeroItems()
    {
        var collection = new Collection("My Collection", "owner-1");
        collection.AddItem(42, At);
        
        collection.RemoveItem(42);

        Assert.Empty(collection.Items);
    }
}