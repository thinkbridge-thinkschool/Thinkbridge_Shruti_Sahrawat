using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using QuotesApi.BackgroundJobs;

namespace Quotes.Tests.Unit;

public class QueuedHostedServiceTests
{
    [Fact]
    public async Task ExecuteAsync_DequeuesAndRunsAQueuedWorkItem()
    {
        var queue = new BackgroundTaskQueue(capacity: 10);
        var logger = Substitute.For<ILogger<QueuedHostedService>>();
        var service = new QueuedHostedService(queue, logger);
        var ran = new TaskCompletionSource();

        await queue.QueueBackgroundWorkItemAsync(_ =>
        {
            ran.SetResult();
            return ValueTask.CompletedTask;
        });

        await service.StartAsync(CancellationToken.None);
        try
        {
            var completed = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.Should().Be(ran.Task, "the hosted service should have dequeued and run the item well within 5 seconds");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopAsync_CompletesPromptly_EvenWhileTheDrainLoopIsBlockedWaitingForWork()
    {
        var queue = new BackgroundTaskQueue(capacity: 10);
        var logger = Substitute.For<ILogger<QueuedHostedService>>();
        var service = new QueuedHostedService(queue, logger);

        // Nothing is ever queued - ExecuteAsync's loop is genuinely parked
        // inside DequeueAsync, the exact state a real shutdown has to unwind
        // cleanly from rather than hang on.
        await service.StartAsync(CancellationToken.None);

        var stopTask = service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(6)));

        completed.Should().Be(stopTask, "graceful shutdown must not hang just because the queue is empty");
        stopTask.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_AThrowingWorkItem_DoesNotStopSubsequentItemsFromRunning()
    {
        var queue = new BackgroundTaskQueue(capacity: 10);
        var logger = Substitute.For<ILogger<QueuedHostedService>>();
        var service = new QueuedHostedService(queue, logger);
        var secondItemRan = new TaskCompletionSource();

        await queue.QueueBackgroundWorkItemAsync(_ => throw new InvalidOperationException("simulated failure"));
        await queue.QueueBackgroundWorkItemAsync(_ =>
        {
            secondItemRan.SetResult();
            return ValueTask.CompletedTask;
        });

        await service.StartAsync(CancellationToken.None);
        try
        {
            var completed = await Task.WhenAny(secondItemRan.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.Should().Be(secondItemRan.Task, "one item throwing must not stop the loop from reaching the next item");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
