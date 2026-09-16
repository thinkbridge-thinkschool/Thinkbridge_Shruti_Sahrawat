[← Back to the day index](../README.md)

## Day 30 — Build day 2: feature completeness

Hit feature completeness. Open a PR for review; respond to comments like you
would on a team — address or push back with reasoning, not silent
force-pushes.

Day 2 of the six-day build plan [Day 28](../day-28/README.md) wrote, and the
plan named it precisely: **a real outbox table, and a non-destructive drain.**
This is the gap the design review opened and refused to hide — the scaffold's
outbox was a queue drained destructively, so a record whose handler threw was
already gone and the retry had nothing to retry. Moving delivery into the
background on Day 28 made that *worse* rather than better, because it removed
the 500 the curator used to receive: the one thing that made a lost message
noticeable.

---

### What changed

| File | What it does now |
|---|---|
| `Outbox/OutboxMessage.cs` | New. The staged event as a row, with a nullable `SentAt` |
| `Outbox/IOutboxStore.cs` | Grew `ReadUnsentAsync` and `MarkSentAsync` — the port was write-only before |
| `Outbox/EfOutboxStore.cs` | New. The outbox as a table in the same DbContext as the aggregate |
| `Outbox/InMemoryOutboxStore.cs` | Deleted |
| `CurationDbContext.cs` | `OutboxMessages` mapped, indexed on `(SentAt, OccurredAt)` |
| `Capstone.Api/InProcessRelay.cs` | Reads unsent, delivers, *then* acknowledges |
| `Capstone.Api/RelayHostedService.cs` | A fresh DI scope per poll |
| `tests/Capstone.Curation.Infrastructure.Tests/` | New project. Five tests over the failure path |

### The one property this day exists to establish

Delivery is acknowledged *after* a subscriber accepts the message, never
before:

```csharp
await sharingHandler.HandleAsync(message, cancellationToken);

await outbox.MarkSentAsync(record.MessageId, cancellationToken);
```

Reversed, those two lines are at-most-once wearing an outbox as a disguise. In
this order, a throw anywhere in `HandleAsync` leaves the row exactly as it
was — still unsent — and the next poll finds it again. Nothing deletes
anything; `SentAt` is the only thing that takes a row out of the relay's
query.

The cost is stated rather than discovered later: a subscriber that accepts a
message and a `MarkSent` that then fails produces a **redelivery**. That is
at-least-once behaving as advertised, and it is why Sharing deduplicating on
`(MessageId, consumer)` stopped being decoration this day and became
load-bearing. There is a test for exactly that.

### The transaction that is finally one transaction

Yesterday's write-up named a gap in its own `UnitOfWork`: `Enqueue` pushed
onto an in-memory queue *before* `SaveChangesAsync`, so a failed commit left
an announcement staged for a state change that had not happened. Today
`Enqueue` is `db.OutboxMessages.Add(...)` — the change tracker, not a queue —
and the single save writes the collection, its items and the outbox row or
writes none of them.

The tell that the gap closed rather than moved: **the ordering of those two
lines stopped mattering.**

### A scope per poll, and why it had to change

`RelayHostedService` is a hosted service, which is a singleton. The outbox is
now read through a scoped `CurationDbContext`. Injecting the relay directly
would have been a captive dependency — one DbContext held for the life of the
process, not thread-safe, with a change tracker growing until restart — so the
service takes `IServiceScopeFactory` and resolves the relay inside a fresh
scope each poll.

That is also the more faithful shape. The real relay opens a connection, reads
a batch, acknowledges it, and lets go.

### Findings

**1 — SQLite cannot `ORDER BY` a `DateTimeOffset`, and the tests are what
caught it.** `ReadUnsentAsync` orders by `OccurredAt`, which the provider
refuses to translate:

```
System.NotSupportedException: SQLite does not support expressions of type
'DateTimeOffset' in ORDER BY clauses. Convert the values to a supported type...
```

Four of five tests failed on it. The one that passed was the only one with no
`ORDER BY` in its query — which is what identified the cause before anything
was changed. `OccurredAt` now stores as **UTC ticks** through a value
converter; a `long` orders identically on both providers. `UtcTicks` and not
`Ticks`, deliberately: `Ticks` is the local wall-clock reading, so two instants
written under different offsets would sort into the wrong order, which is the
one bug a time-ordered queue cannot afford.

Worth noting where this would have surfaced without the tests: the
`/api/outbox` endpoint added the same day orders by the same column, so the
first request to it would have thrown. This is [Day 29](../day-29/README.md)'s
finding again — a model both providers accept at migration time and only one
accepts at query time.

**2 — The poll is no longer free, and it is now visible.** Against an
in-memory queue the 250ms interval cost nothing. Against a table it is a query
four times a second, forever, almost always returning nothing — and with EF
logging commands at Information it buried every other line in the console:

```
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      SELECT "o"."MessageId", "o"."EventType", "o"."Payload", "o"."OccurredAt"
      FROM "OutboxMessages" AS "o"
      WHERE "o"."SentAt" IS NULL
      ORDER BY "o"."OccurredAt"
      LIMIT @p
      ... repeated indefinitely
```

This is precisely the pattern [Day 26](../day-26/README.md)'s dependency
breakdown found in the main solution — 366 SQLite calls in half an hour from a
five-second poll — arriving here on schedule. `Microsoft.EntityFrameworkCore
.Database.Command` is now at `Warning`, because a log that drowns everything
is a log nobody reads. The underlying cost is unaddressed and is the strongest
argument for day 3 happening on time: a broker is pushed to, not polled.

**3 — The unsent state is observable, and only by accident.** The walkthrough
below captured a row as `delivered: false` and then the same row as
`delivered: true`. That was not planned — the prediction in this session was
that racing a 250ms poll by hand is impractical. Pasting a block of commands
rather than typing them runs them back-to-back in single-digit milliseconds,
which is comfortably inside the window. The timestamps say how wide it was:

```
occurredAt  07:27:18.6127137
sentAt      07:27:18.9384133
                     325.7 ms
```

One poll interval plus scheduling. Useful as illustration, and worthless as a
guarantee — which is the point. The *retry* path cannot be shown this way at
all, because it needs a subscriber that fails on demand. That is what the
tests are for, and why they are the evidence that matters here rather than the
curl transcript.

**4 — The system is now half-persisted, and that asymmetry is a real hazard.**
The outbox row survived a process restart. Bob's follow did not —
`InMemoryFollowerDirectory` and `InMemoryFeedWriter` are per-process
singletons. So a restart leaves durable announcements pointed at subscribers
that no longer exist, and the relay will deliver a message to zero followers
and correctly mark it sent. Nothing is lost that the design promised, and the
observable result — a published collection nobody's feed reflects — looks
exactly like the bug this day fixed. Day 4 of the plan removes the asymmetry.

### Proof

The tests, which are the real evidence:

```
$ dotnet test tests\Capstone.Curation.Infrastructure.Tests
Test summary: total: 5, failed: 0, succeeded: 5, skipped: 0

  Publishing_commits_the_collection_and_its_outbox_row_together
  A_subscriber_that_throws_leaves_the_row_unsent
  The_next_drain_delivers_the_row_once_the_subscriber_recovers
  A_delivered_row_is_not_delivered_again
  A_redelivered_message_fans_out_once
```

The second and third are the plan's "done when" stated as code: a feed store
that throws on demand leaves the row unsent, and the retry delivers it once
the store recovers. The whole suite, unchanged and still green:

```
$ dotnet test tests\Capstone.Curation.Domain.Tests
Test summary: total: 20, failed: 0, succeeded: 20, skipped: 0

$ dotnet test tests\Capstone.ArchitectureTests
Test summary: total: 6, failed: 0, succeeded: 6, skipped: 0
```

The migration, with the converted column:

```
CREATE TABLE "OutboxMessages" (
    "MessageId" TEXT NOT NULL CONSTRAINT "PK_OutboxMessages" PRIMARY KEY,
    "EventType" TEXT NOT NULL,
    "Payload" TEXT NOT NULL,
    "OccurredAt" INTEGER NOT NULL,
    "SentAt" TEXT NULL
);
CREATE INDEX "IX_OutboxMessages_SentAt_OccurredAt" ON "OutboxMessages" ("SentAt", "OccurredAt");
```

And the row, in both states:

```powershell
PS> Invoke-RestMethod http://localhost:5000/api/outbox | ConvertTo-Json
{
    "value":  [
                  {
                      "messageId":  "01a0a91c-e658-78c3-aaf5-83cd22e6a444",
                      "eventType":  "curation.collection.published.v1",
                      "occurredAt":  "2026-09-16T07:27:18.6127137+00:00",
                      "sentAt":  null,
                      "delivered":  false
                  }
              ]
}

PS> Invoke-RestMethod http://localhost:5000/api/outbox | ConvertTo-Json
{
    "value":  [
                  {
                      "messageId":  "01a0a91c-e658-78c3-aaf5-83cd22e6a444",
                      "eventType":  "curation.collection.published.v1",
                      "occurredAt":  "2026-09-16T07:27:18.6127137+00:00",
                      "sentAt":  "2026-09-16T07:27:18.9384133+00:00",
                      "delivered":  true
                  }
              ]
}
```

Same `messageId`, same `occurredAt`, `sentAt` filled in. The row was never
deleted, moved, or replaced — the only thing that changed is the
acknowledgement, which is the entire design in one field.

### The review

- [PR #8](https://github.com/thinkbridge-thinkschool/Thinkbridge_Shruti_Sahrawat/pull/8) — the outbox itself
- [PR #9](https://github.com/thinkbridge-thinkschool/Thinkbridge_Shruti_Sahrawat/pull/9) — running its tests in CI

**Where the review actually came from, stated plainly.** Copilot code review is
not enabled on this organisation, and the mentor's pass runs on a weekly cycle,
so neither PR carries a GitHub review thread yet. Both have a review requested
and this section will carry that exchange when it lands. What follows is the
feedback this work did receive — from paired review during the session — and it
is labelled as that rather than dressed up as something it was not. Day 28 took
the same position about its own critique, for the same reason: a review whose
source is misdescribed is worth less than no review.

**Changed — the drain's query could not run on the default provider.** The
first review point was that `ReadUnsentAsync` orders by `OccurredAt`, a
`DateTimeOffset`, which SQLite refuses to translate in an `ORDER BY`. Four of
the five new tests failed on it. Changed rather than argued: `OccurredAt` now
persists as UTC ticks through a value converter. The reasoning went into the
mapping rather than the commit message alone, because the *next* person to add
a timestamp column to this table needs it at the point they are typing.

**Changed — a new test project that CI never ran.** PR #8 added
`Capstone.Curation.Infrastructure.Tests` and did not add it to `ci.yml`'s
capstone matrix, so the five tests that are the entire evidence for this day
passed locally while CI reported green on a suite that did not contain them.
Raised after PR #8 had already merged; fixed in [PR #9](https://github.com/thinkbridge-thinkschool/Thinkbridge_Shruti_Sahrawat/pull/9)
rather than quietly amended onto the first one, which would have rewritten
history that was already on `main`.

**Defended — the CI matrix stays an explicit list, not a glob.** The obvious
response to the gap above is `capstone/tests/*`, so it cannot happen again.
Pushed back, and the argument is the one this repository already makes
elsewhere: `ModuleBoundaries.Allowed` is a hand-maintained table precisely so
that widening the graph is a decision somebody takes rather than a thing that
happens. A glob applies the opposite default to CI, and would silently enrol
any future project — including one written to demonstrate a failure — into the
gate.

The counter-argument is real and is recorded rather than dismissed: a boundary
table defends against deliberate mistakes, a CI matrix defends against
*forgetting*, and those two want opposite defaults. The position taken here is
that the forgetting is visible (a missing job on the run page) while a glob's
over-inclusion is not, so the failure mode of the explicit list is the one that
gets noticed. The comment added to `ci.yml` names the cost in full, so a
reviewer who disagrees is arguing with a stated position rather than
discovering an accident.

**What was not done, and matters as much:** nothing was force-pushed. PR #8
stayed exactly as merged, and the correction landed as its own commit with its
own reasoning — because a review comment that a force-push makes disappear is a
conversation someone had and can no longer point at.

### What did you learn this session?

That "the outbox commits with the state change" was a sentence in a design
document for two days before it was true in code, and the thing that made it
true was not new logic - it was moving one `Add` into the same change tracker
so the ordering of two lines stopped mattering.

### What would break this?

**A row that can never succeed is now retried forever.** The old destructive
drain lost poison messages; this one retries them four times a second with
nothing counting attempts and nothing giving up. Better, and not yet correct —
the answer is the dead-letter path Day 19 already built, which day 3 inherits.

**Ordering is by timestamp, which is the weaker choice.** Two events staged in
the same commit can share an `OccurredAt` and their relative order is then
undefined. A production outbox orders by a monotonic sequence; that is not here
because SQLite only auto-increments an `INTEGER PRIMARY KEY`, so a sequence
would mean demoting `MessageId` from key to unique index. Named rather than
assumed away.

### GitHub link

https://github.com/thinkbridge-thinkschool/Thinkbridge_Shruti_Sahrawat/tree/main/Days/day-30
