using Azure.Messaging.ServiceBus;
using Quotes.Messaging;

namespace Quotes.Worker;

/// <summary>
/// Prints what is sitting in each subscription's dead-letter queue.
/// </summary>
/// <remarks>
/// <para>The dead-letter queue is a real sub-queue of the subscription, at
/// <c>&lt;topic&gt;/Subscriptions/&lt;subscription&gt;/$deadletterqueue</c>, not
/// a log or a metric. Messages sit there whole - body, properties and all -
/// until something takes them out, which is what makes them recoverable: fix
/// the bug, then resubmit.</para>
///
/// <para>This peeks rather than receives. Receiving would lock each message and
/// eventually remove it, so an inspection tool would quietly destroy the
/// evidence it was written to show. Peek is read-only and repeatable.</para>
///
/// <para>Nothing drains this queue automatically. It has no consumer, it counts
/// against the entity's quota, and a subscription can fill up with dead letters
/// while every dashboard stays green - which is why the real production concern
/// here is an alert on dead-letter message count, not the queue itself.</para>
/// </remarks>
public static class DeadLetterInspector
{
    public static async Task RunAsync(IServiceProvider services)
    {
        var settings = services.GetRequiredService<ServiceBusSettings>();
        var client = services.GetRequiredService<ServiceBusClient>();

        foreach (var subscription in new[] { settings.SearchIndexerSubscription, settings.AuditLogSubscription })
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 78));
            Console.WriteLine($"DEAD-LETTER QUEUE: {settings.TopicName}/Subscriptions/{subscription}");
            Console.WriteLine(new string('=', 78));

            await using var receiver = client.CreateReceiver(
                settings.TopicName, subscription,
                new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

            var messages = await receiver.PeekMessagesAsync(maxMessages: 50);

            if (messages.Count == 0)
            {
                Console.WriteLine("  (empty)");
                continue;
            }

            foreach (var message in messages)
            {
                Console.WriteLine();
                Console.WriteLine($"  MessageId                 : {message.MessageId}");
                Console.WriteLine($"  DeadLetterReason          : {message.DeadLetterReason}");
                Console.WriteLine($"  DeadLetterErrorDescription: {message.DeadLetterErrorDescription}");
                Console.WriteLine($"  DeliveryCount             : {message.DeliveryCount}");
                Console.WriteLine($"  EnqueuedTime              : {message.EnqueuedTime:u}");
                Console.WriteLine($"  Body                      : {Shorten(message.Body.ToString(), 100)}");
            }

            Console.WriteLine();
            Console.WriteLine($"  {messages.Count} message(s) dead-lettered on {subscription}.");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Drains both dead-letter queues, so a demo run starts from empty.
    /// </summary>
    /// <remarks>
    /// Needed because nothing drains a dead-letter queue on its own - that is
    /// the whole point of it. Messages sit there until something deliberately
    /// takes them out, so without this a second demo run shows the first run's
    /// dead letters alongside its own and the evidence stops being readable.
    ///
    /// This is also, in miniature, what a real dead-letter remediation tool
    /// does: receive from the sub-queue and settle. A production one would
    /// republish to the main entity after a fix rather than completing and
    /// discarding, which is the one line of difference between "drain" and
    /// "lose the messages".
    /// </remarks>
    public static async Task PurgeAsync(IServiceProvider services)
    {
        var settings = services.GetRequiredService<ServiceBusSettings>();
        var client = services.GetRequiredService<ServiceBusClient>();

        foreach (var subscription in new[] { settings.SearchIndexerSubscription, settings.AuditLogSubscription })
        {
            await using var receiver = client.CreateReceiver(
                settings.TopicName, subscription,
                new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

            var purged = 0;
            try
            {
                while (true)
                {
                    // A short wait, not the default 60s: an empty queue is the
                    // expected case here and should not stall the script for a
                    // minute per subscription to discover it.
                    var batch = await receiver.ReceiveMessagesAsync(50, TimeSpan.FromSeconds(3));
                    if (batch.Count == 0) break;

                    foreach (var message in batch)
                    {
                        await receiver.CompleteMessageAsync(message);
                        purged++;
                    }
                }
            }
            catch (ServiceBusException ex)
            {
                // Purging is housekeeping, not the exercise. An unreachable or
                // not-yet-created namespace here is worth reporting, but
                // letting it terminate the process means a cleanup step that
                // failed takes down a run that had not started yet - and the
                // stack trace it printed named this method rather than the
                // actual problem, which is that the broker is not there.
                Console.Error.WriteLine(
                    $"  could not drain {subscription}: {ex.Reason}. Continuing - the run will fail later " +
                    "with a clearer error if the namespace really is unreachable.");
                return;
            }

            Console.WriteLine($"  purged {purged} dead-lettered message(s) from {subscription}");
        }
    }

    private static string Shorten(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";
}
