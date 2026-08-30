[← Back to full README](../../README.md)

## Day 18 — Background jobs

**Move slow work off the request thread.** [`QuotesApi/BackgroundJobs/IBackgroundTaskQueue.cs`](../../QuotesApi/BackgroundJobs/IBackgroundTaskQueue.cs) · [`BackgroundTaskQueue.cs`](../../QuotesApi/BackgroundJobs/BackgroundTaskQueue.cs) · [`QueuedHostedService.cs`](../../QuotesApi/BackgroundJobs/QueuedHostedService.cs)

A bounded `System.Threading.Channels` queue (capacity 100, `FullMode.Wait`) and a `BackgroundService` that drains it. An endpoint enqueues a work item and returns immediately; the hosted service dequeues on its own schedule, off the request thread entirely — `POST /api/demo/queue-work?delayMs=3000` in [`Program.cs`](../../QuotesApi/Program.cs) demonstrates the handoff with a job that has nothing real to compute, just sleeps and logs.

**Graceful shutdown, proven rather than assumed.** `QueuedHostedService.ExecuteAsync` loops on `DequeueAsync(stoppingToken)`. On shutdown the host cancels `stoppingToken`; a pending dequeue throws `OperationCanceledException` and the loop exits on its own rather than being killed mid-iteration. [`StopAsync_CompletesPromptly_EvenWhileTheDrainLoopIsBlockedWaitingForWork`](../../Quotes.Tests.Unit/QueuedHostedServiceTests.cs) proves this directly: it starts the service with an empty queue — the loop genuinely parked inside `DequeueAsync` — then calls `StopAsync` and asserts it completes within 5 seconds rather than hanging.

**A failing job doesn't take the loop down.** [`ExecuteAsync_AThrowingWorkItem_DoesNotStopSubsequentItemsFromRunning`](../../Quotes.Tests.Unit/QueuedHostedServiceTests.cs) queues a work item that throws, then a second one that should still run — and confirms the second one does, with the exception logged rather than silently swallowed or left to kill every job after it.

**When Hangfire over a hosted service.** Reach for Hangfire when the job must survive a process restart, needs a real schedule (cron/recurring), or needs retries and a dashboard you didn't write yourself. A `BackgroundService` draining an in-memory queue only exists as long as the process does — restart it and every unprocessed item is simply gone, since nothing here is written to durable storage.

**Verified, not assumed.** `dotnet test Quotes.Tests.Unit` — **129 tests passing, 0 failed** (123 pre-existing + 6 new: 3 for `BackgroundTaskQueue`'s ordering and cancellation behaviour, 3 for `QueuedHostedService`'s dequeue/run, graceful shutdown, and failure-isolation behaviour). `QuotesApi` itself still builds clean.

No screenshots for this one — the exercise is proven by tests and the build, not by a live call to a since-deleted Azure resource.
