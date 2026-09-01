[← Back to full README](../../README.md)

## Day 19 — Azure Service Bus topics + DLQ

Publish to a topic with two subscriptions, consume with a competing-consumer
worker, make the handlers idempotent on a message id, and show a poison message
landing in the dead-letter queue.

**One publish, two consumers that have never heard of each other.**
[`servicebus-emulator/config.json`](../../servicebus-emulator/config.json)
defines the topic `quote-events` and two subscriptions on it. A queue would
have made this exercise impossible: a queue has exactly one logical reader, so
the search indexer and the auditor would have had to be the same code, or the
publisher would have had to know both existed and send twice. A topic inverts
that - the publisher names one destination and the broker copies each message
to every subscription. Adding a third consumer tomorrow is a config change with
no publisher deployment.

The two subscriptions are deliberately not mirror images.
`search-indexer` carries a SQL filter on the event type - written
`user.eventType = 'QuoteCreated'` in the emulator config, where the `user.`
qualifier is explicit, and `eventType = 'QuoteCreated'` on the real
subscription, where the unqualified name resolves against application
properties anyway; `audit-log` carries a catch-all. So a `QuoteDeleted` event reaches the auditor
and never reaches the indexer at all - the broker discards it before delivery
rather than the indexer receiving and ignoring it. Filters run server-side,
which is the point: the traffic an unfiltered subscriber would receive and
throw away never crosses the network.

Those filters match against *application properties*, never the body, which is
why [`ServiceBusQuoteEventPublisher`](../../Quotes.Messaging/Publishing/ServiceBusQuoteEventPublisher.cs)
promotes `eventType` out of the payload and onto the message. A routing key
buried in the JSON is invisible to the broker.

**Competing consumers is a different mechanism from fan-out, and the exercise
needs both.** Fan-out is across subscriptions: two subscriptions, each gets its
own copy of every message. Competing consumers is *within* one subscription:
several worker instances read `search-indexer`, and the broker gives each
message to exactly one of them. One is about independent concerns, the other is
about throughput, and they compose - which is what running two copies of
[`Quotes.Worker`](../../Quotes.Worker/) demonstrates. Neither instance
coordinates with the other or knows how many others exist;
[`SubscriptionProcessor`](../../Quotes.Messaging/Consuming/SubscriptionProcessor.cs)
is identical in both.

**At-least-once delivery is the entire reason idempotency is on this exercise.**
Under peek-lock the broker hands over a message and holds a lock until the
consumer settles it. Do the work, then die before completing - or merely take
longer than the lock duration - and the lock lapses and the message is
redelivered. The broker is behaving correctly; from its side nothing was ever
acknowledged. Exactly-once delivery is not a setting that can be switched on.
Exactly-once *effect* is something the consumer builds.

**The obvious implementation is wrong in two separate ways.** Check-then-act -
ask whether this id was seen, and if not do the work and record it - fails
because two competing consumers can both pass the check before either records
anything, and because a crash between doing the work and recording it leaves
the work done and unrecorded. Both holes are the same hole: the check and the
effect are not one operation.
[`QuoteEventDispatcher`](../../Quotes.Messaging/Consuming/QuoteEventDispatcher.cs)
stages the ledger row and the handler's work on one context and writes them in
a single `SaveChanges`, so EF Core commits them in one transaction. Uniqueness
is enforced by a composite primary key, which puts the arbitration in the
database - the only participant that can actually serialise two racing writers.
The loser gets a constraint violation, and on the ledger key that violation is
not an error, it is the answer.

**The dedupe key is `(MessageId, Consumer)`, not `MessageId`.** This is the
subtle one. A topic fans one message out to every subscription, so the same
message id legitimately arrives at both consumers and both are supposed to act
on it. Key the ledger on the message id alone and whichever consumer runs
second finds a row already there, concludes "duplicate", and skips work it was
always meant to do - with no exception, no retry and no dead-letter. The audit
trail just quietly develops holes.
[`DispatchAsync_WhenTwoDifferentConsumersGetTheSameMessage_BothDoTheirOwnWork`](../../Quotes.Tests.Unit/QuoteEventDispatcherTests.cs)
is the regression test for exactly that.

**Deduplication starts at the publisher, not the consumer.**
[`QuoteEventIds`](../../Quotes.Messaging/Publishing/IQuoteEventPublisher.cs)
derives the message id from the event - type, entity, instant - rather than
generating one per send. With `Guid.NewGuid()` a publisher retry after a lost
acknowledgement produces a second message that is, as far as anything
downstream can tell, a different event; the consumer's ledger sees an id it has
never seen and does the work twice, entirely correctly. No amount of
consumer-side care recovers from an id that was already wrong when it left the
publisher.

Broker-side duplicate detection is left **off** in the emulator config on
purpose. Turning it on would have the broker silently swallow the replayed
message, and the consumer-side protection - the part this exercise is actually
about - would never be exercised. It also solves a different problem than it
appears to: it dedupes at *publish* time within a bounded window, which guards
against a publisher retry, and does nothing at all about redelivery to a
consumer after a lock expiry.

**Two roads to the dead-letter queue, and a handler has to tell them apart.**
[`PoisonMessageException`](../../Quotes.Messaging/Consuming/PoisonMessageException.cs)
marks the failures that can never succeed - a body that is not JSON, a
negative id, an event type this consumer cannot handle. Those are
dead-lettered on the first delivery with a reason attached, because the body is
not going to become valid JSON on the fourth attempt and burning three lock
durations to discover that only buries the real diagnosis under identical
repeated errors. Every other exception is treated as possibly transient: the
message is abandoned, redelivered, and if it keeps failing the *broker*
dead-letters it once `DeliveryCount` passes `MaxDeliveryCount` - set to 3 here
so the demo takes seconds rather than ten rounds. Getting the classification
backwards is expensive in both directions: dead-lettering real work during a
two-second outage, or retrying a malformed message ten times for nothing.

**What this actually ran against, and the emulator that is still here.**
Service Bus topics do not exist below the Standard tier - Basic supports queues
only - so "publish to a topic" is never free. The plan was Microsoft's local
emulator ([`docker-compose.yml`](../../servicebus-emulator/docker-compose.yml)),
which serves the same AMQP surface including subscriptions, SQL filters,
delivery counts and dead-letter queues at no cost; its first image pull stalled
past twenty minutes on a slow connection, so the verified run went against a
real Standard-tier namespace instead. Full details, including the exact `az`
commands and the teardown, are in [`AZURE-NOTES.md`](AZURE-NOTES.md).

That switch changed configuration only, not code.
[`ServiceBusClientFactory`](../../Quotes.Messaging/Publishing/ServiceBusClientFactory.cs)
already preferred a namespace plus `DefaultAzureCredential` over a connection
string whenever both were available, so pointing
[`appsettings.json`](../../Quotes.Worker/appsettings.json) at the real namespace
was enough - and it means the run authenticated with no key at all, the same
credential-based shape the API uses for Azure SQL, resolving locally to the
`az login` session. The emulator files remain because they are the version
anyone can re-run for free; note that
[`day19-demo.ps1`](../../scripts/day19-demo.ps1) starts the emulator by default
and needs `-SkipEmulatorStart` when `FullyQualifiedNamespace` is set.

**What this deliberately does not do.** The publisher is not wired into
`QuotesApi`. `POST /api/quotes` does not raise a `QuoteCreated` - the only thing
that publishes is [`DemoPublisher`](../../Quotes.Worker/DemoPublisher.cs), a
console verb. That is a deliberate limit rather than an oversight: the API is
deployed and serving real data, and adding an outbound dependency on a
Service Bus namespace that gets deleted at the end of the day would have put a
live application one missing resource away from failing to start. Wiring it in
properly needs the outbox pattern anyway - publishing inside the same
transaction as the quote insert, rather than after it, so a crash between the
two cannot leave a quote that no consumer ever hears about - and that is a
larger piece of work than this exercise asked for.

**Verified, not assumed.** `dotnet test Quotes.Tests.Unit` — **188 tests passing,
0 failed** (174 pre-existing + 14 new: idempotent replay, the competing-consumer
race, the same message across two different consumers, no partial write when a
handler rejects a message, poison-versus-transient classification, and message-id
determinism). The run itself is captured in [`test-run.txt`](test-run.txt) rather than left as
a number in prose. The idempotency tests run against a real SQLite file rather
than EF Core's InMemory provider, which enforces no unique constraints at all —
every one of them would pass against InMemory while proving nothing, because the
mechanism under test *is* a database constraint rejecting a second insert.

The live run against a real Service Bus namespace is captured in
[`evidence.txt`](evidence.txt) — 10 messages published, two worker instances:

| What the exercise asks for | What the run shows |
|---|---|
| A topic with two subscriptions | `search-indexer` recorded 6, `audit-log` recorded 9 — the gap is one message the SQL filter excluded plus two `search-indexer` dead-lettered and the auditor accepted |
| A competing-consumer worker | `search-indexer` split worker-1=2 / worker-2=4; `audit-log` split worker-1=4 / worker-2=5 |
| Handlers idempotent on a message id | 10 published, 15 ledger rows (9 + 6), the replay recorded once per consumer and no second effect |
| A poison message in the DLQ | Two, by both routes: `InvalidQuoteId` at `DeliveryCount` 0 and `MaxDeliveryCountExceeded` at `DeliveryCount` 3 |

Where each message went, exactly. Ten were published. `audit-log`'s rule is a
catch-all, so it received all ten and recorded nine — the missing one is the
replay. `search-indexer`'s filter excluded the `QuoteDeleted`, leaving nine
delivered; it recorded six, dead-lettered two, and rejected one as a duplicate.
Nothing is unaccounted for on either side.

The ledger row counts alone would not prove the replay was caught, because a
message that was never delivered twice produces exactly the same totals. That
is why the evidence file carries the workers' own log lines under
**IDEMPOTENCY: REDELIVERIES REJECTED** — `Duplicate ignored: … had already
processed messageId=QuoteCreated-101-…`, emitted by
[`QuoteEventDispatcher`](../../Quotes.Messaging/Consuming/QuoteEventDispatcher.cs)
at the moment the composite key rejected the second insert. The distinction
matters: the counts are consistent with idempotency working, the log lines are
evidence that it did.

`audit-log`'s dead-letter queue is **empty**. The two messages `search-indexer`
rejected were processed normally by the auditor, because each subscription
keeps its own delivery counts and its own dead-letter queue. Fan-out means
genuinely independent fates, not two copies of one outcome.

**What would break this.**

The idempotency guarantee is exactly as strong as the transaction it rides on.
Every effect here is a row in the same SQLite database as the ledger, so one
`SaveChanges` covers both. A handler that also called an external API, sent an
email or wrote to a second database would have put an effect outside that
transaction, and no ledger can make that atomic — it needs the outbox pattern
or a genuinely idempotent downstream.

The ledger grows without bound. Nothing here prunes it, and rows older than the
longest possible redelivery window are dead weight; a real deployment needs a
retention job, and choosing that retention window wrongly silently reopens the
duplicate hole.

Competing consumers destroy ordering. Any two messages can be processed in
either order, or simultaneously. Nothing in this exercise depends on order, but
a workflow that did would need sessions — and sessions pin a session id to one
consumer, which gives back the ordering by taking away most of the parallelism.

Nothing drains the dead-letter queue. It has no consumer, it counts against the
entity's quota, and a subscription can silently fill with dead letters while
every dashboard stays green. The production requirement is an alert on
dead-letter message count; the `purge-dlq` command here is a demo convenience,
and a real remediation tool would republish messages after a fix rather than
completing and discarding them.

Lock duration is a hidden coupling. It is 30 seconds in the emulator config and
the Azure default of 60 on the real subscriptions - deliberately never tuned,
because the handlers finish in milliseconds and it never binds. A handler that grew slower
than the lock would have its message redelivered *while it was still working on
it* — the work would complete, the completion would fail on an expired lock,
and the delivery count would climb toward the dead-letter queue for messages
that were succeeding all along.

**A bug this exercise caught.** The first live run recorded all 15 of its
ledger rows against `worker-2`, with `worker-1` credited with nothing at all. The cause was not messaging at all: both instances started at the same
instant against a freshly deleted SQLite file and raced to create the schema.
`EnsureCreated` is check-then-create rather than atomic, so the loser threw and
the process exited before it ever reached its consuming loop — while the
publisher and the survivor carried on looking perfectly healthy. The output was
indistinguishable from "competing consumers work, one instance was just
faster". Fixed by creating the schema once before either worker starts, and by
retrying it inside the worker rather than letting a startup race kill the
process. Worth recording because the failure was silent, looked like success,
and would have been invisible without checking *which* instance did the work.
