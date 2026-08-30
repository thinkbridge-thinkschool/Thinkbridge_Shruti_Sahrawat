using FluentAssertions;
using QuotesApi.BackgroundJobs;

namespace Quotes.Tests.Unit;

public class BackgroundTaskQueueTests
{
    [Fact]
    public async Task DequeueAsync_ReturnsItemsInTheOrderTheyWereQueued()
    {
        var queue = new BackgroundTaskQueue(capacity: 10);
        var order = new List<int>();

        await queue.QueueBackgroundWorkItemAsync(_ => { order.Add(1); return ValueTask.CompletedTask; });
        await queue.QueueBackgroundWorkItemAsync(_ => { order.Add(2); return ValueTask.CompletedTask; });

        var first = await queue.DequeueAsync(CancellationToken.None);
        var second = await queue.DequeueAsync(CancellationToken.None);

        await first(CancellationToken.None);
        await second(CancellationToken.None);

        order.Should().Equal(1, 2);
    }

    [Fact]
    public async Task DequeueAsync_OnAnEmptyQueue_ThrowsAsSoonAsTheTokenIsCancelled()
    {
        var queue = new BackgroundTaskQueue(capacity: 10);
        using var cts = new CancellationTokenSource();

        var dequeueTask = queue.DequeueAsync(cts.Token).AsTask();
        dequeueTask.IsCompleted.Should().BeFalse("nothing has been queued yet, so this should genuinely be waiting");

        cts.Cancel();

        var act = async () => await dequeueTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task QueueBackgroundWorkItemAsync_NullWorkItem_Throws()
    {
        var queue = new BackgroundTaskQueue(capacity: 10);

        Func<Task> act = async () => await queue.QueueBackgroundWorkItemAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
