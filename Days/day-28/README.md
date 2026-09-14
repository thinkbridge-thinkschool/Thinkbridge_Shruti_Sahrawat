[← Back to the day index](../README.md)

## Day 28 — Design review + ADR

Mentor and peer critique of the capstone design. Write the ADR for the one
decision that matters most — the trade-off, the alternatives, the reasoning —
and plan the build day by day.

The design under review is the capstone from Day 22:
[curated collections, published to followers](../../capstone/README.md).

---

### The decision that matters most

[**ADR-0001 — Capstone modules are assemblies, and the dependency graph is a
test**](../../docs/adr/0001-modules-as-assemblies-enforced-by-tests.md)

Three bounded contexts, one slice, and the question that decides everything
else: what stops the three from quietly becoming one. This repository already
contains the counter-example, which is what makes the decision concrete rather
than theoretical — `QuotesApi` is layered by folder inside a single assembly,
and nothing there stops `Domain` referencing `Data` except that nobody has done
it yet. That is a social property of the team, not a structural property of the
code. It survives one careful developer and does not survive a deadline.

The record works through four alternatives — folders in one assembly, assemblies
with no enforcement, three deployable services, and an off-the-shelf
architecture-testing library — and says what each would cost. The short version
of the trade: twelve projects for one feature slice, paid once at setup in a
currency that does not compound, bought against a coupling cost that accrues
invisibly every week and comes due on the day someone finally needs the modules
separated.

It is the decision that matters most because every other decision in the
capstone is downstream of it. Separate domain and integration event types,
`Contracts` projects existing at all, the translator being a single file, the
publish handler doing the cross-context check instead of the aggregate — none of
those are free-standing choices. They are consequences.

---

### The top critique, and how it changed the design

**The critique.** *The design's strongest claim is contradicted by its own
composition root.*

`capstone/README.md` states the promise plainly: "The commit is the boundary of
the promise. The curator's request returns once the state change and the outbox
row are committed together. Everything after that is catch-up." It then gives
the reason — publishing inside the transaction "would tie one curator's publish
latency to their follower count and fail the publish outright when the feed
store is unavailable."

`Program.cs` did the second thing.

```csharp
// before — Program.cs, the publish endpoint
await handler.HandleAsync(new PublishCollectionCommand(id, request.CuratorId), cancellationToken);

var delivered = await relay.DrainAsync(cancellationToken);

return Results.Ok(new { published = true, messagesRelayed = delivered });
```

Reading that line by line against the claim: the handler commits, so at that
point the publish has genuinely happened and is durable. Then the request waits
for the relay to drain, which walks every staged message and calls Sharing's
handler, which writes one feed row per follower. Only then does the caller get a
response. So publish latency is a function of follower count, exactly as the
design said it must not be. Worse, if Sharing throws — a full feed store, a
deadlock, anything — the exception propagates out of the endpoint and the curator
receives an error for an operation that already succeeded and cannot be undone.
The collection is published; the response says it failed. That is the most
expensive class of bug in the whole design, and it was in the one endpoint the
whole capstone exists to demonstrate.

The comment sitting above it made it worse rather than better:

```csharp
// Draining inline is the scaffold's shortcut, not the design - see InProcessRelay.
```

That is the same mistake as layering by folder. The module boundary was made
structural and given a test that fails the build. The async boundary was left as
prose, in a comment, next to code doing the opposite. A convention is not a
boundary — that is the repository's own position, stated in
[`ModuleBoundaries`](../../capstone/tests/Capstone.ArchitectureTests/ModuleBoundaries.cs)
and in ADR-0001, and it was not being applied evenly.

**How the design changed.** The drain moved off the request path into
[`RelayHostedService`](../../capstone/src/Capstone.Api/RelayHostedService.cs),
a background loop that polls the outbox. The publish endpoint now returns the
moment the commit is done and reports no delivery count at all:

```csharp
// after — Program.cs, the publish endpoint
await handler.HandleAsync(new PublishCollectionCommand(id, request.CuratorId), cancellationToken);

return Results.Ok(new { published = true });
```

Dropping `messagesRelayed` from the response was not cosmetic. A caller who can
read a delivery count will eventually depend on it, and the moment anything
depends on it the fan-out is back on the publish path — through a contract this
time instead of a call, which is harder to remove. The honest response says the
one thing the server actually promised.

The loop polls rather than being signalled by the writer, deliberately. The
relay this scaffolds is a separate process reading a table it shares no memory
with, and it has no way to be woken by the publisher either. Keeping that
property means the eventual swap to the Day 20 Service Bus relay changes where
the relay reads from and nothing about how it is driven.

Two honest consequences follow, and both are improvements in what the scaffold
demonstrates. A follower's feed is now observably eventually consistent — read
it the instant publish returns and it can legitimately be empty, which is the
design being visible rather than a bug. And a failure inside Sharing no longer
reaches the curator at all; it is logged and retried on the next poll, because a
relay that dies on one bad message stops delivering every later one and turns a
single poison record into a total outage of the fan-out.

**The gap this opened, recorded rather than hidden.**
[`InMemoryOutboxStore.DrainPending`](../../capstone/src/Modules/Curation/Capstone.Curation.Infrastructure/Outbox/InMemoryOutboxStore.cs)
dequeues destructively, so a record whose handler throws is gone — the retry on
the next poll has nothing left to retry. Inline, that was survivable, because the
exception at least reached a human through a 500. In the background it is
silent, which makes the same flaw worse. The real outbox marks `SentAt` only
after the broker acknowledges, which is what makes at-least-once true there and
not yet here. That is day 2 of the build plan below, and it is the first thing
that would be wrong to ship.

**Mentor review.** The mentor reviews each week's work on its own cycle; this
record will carry their critique of the capstone design verbatim beneath this
line when it arrives, along with what it changed. The critique above came out of
reviewing the design against its own ADR while writing the ADR, which is most of
what a design review is for — the act of writing down why a decision was right
is what exposes the place the code stopped honouring it.

---

### The build plan, day by day

Six days from the current scaffold to something deployable on the infrastructure
Days 23 through 26 already built. Ordered so that every day ends with the tests
green and nothing half-migrated, and so that the riskiest unknown — real
persistence under the aggregate — is met first rather than last.

**Day 1 — EF Core persistence for the Curation module.**
`Collection` becomes a mapped entity, `Items` an owned collection, and
`CollectionId` / `CuratorId` / `QuoteId` pass through value converters so the
domain keeps its own types and the database sees primitives. The domain-minted
Guid v7 id does the work it was chosen for here: no `SaveChanges` is needed to
obtain an identity, so the state change and the outbox row stay in one
transaction. Touches `Capstone.Curation.Infrastructure` only — the domain must
not acquire an EF reference, and `DeclaredReferenceTests` will fail the build if
it does, which is the day's real safety net.
*Done when* the 20 domain tests still pass untouched and the same publish
walkthrough works against SQL rather than a dictionary.

**Day 2 — A real outbox table, and a non-destructive drain.**
The gap the design review opened. `IOutboxStore` grows an explicit acknowledge
step: the relay reads unsent rows, delivers, then marks `SentAt`, so a handler
that throws leaves the row unsent and the next poll retries it. This is the
change that makes at-least-once true rather than claimed, and it is why Sharing
deduplicating on `(MessageId, consumer)` stops being decoration.
*Done when* a deliberately failing Sharing handler leaves the row unsent, and a
test proves the retry delivers it once the handler recovers.

**Day 3 — Replace the in-process relay with the Service Bus one.**
Days 19 and 20 already built and tested this: a separate process, a topic, a
subscription, dead-lettering. `RelayHostedService` is the seam, and by design
the only thing that changes is where the relay reads from and where it
publishes. Sharing's handler does not change at all, which is the claim the
in-process version existed to make checkable.
*Done when* publish in one process puts a message on the topic and a separately
started subscriber writes the feed, with the poison-message path landing in the
DLQ rather than in a log line.

**Day 4 — Catalog against the real quote tables, and Sharing persistence.**
`InMemoryQuoteCatalog` gives way to an adapter over the existing quote tables,
which is a change confined to `Capstone.Catalog.Infrastructure` because
`IQuoteCatalog` is the published language and Curation holds only `QuoteId`.
Follows, feeds and the processed-message log become tables in the same pass.
*Done when* publishing a collection containing a deleted quote fails with the
same message it does today, now for a real reason.

**Day 5 — The read side, and the fan-out ceiling.**
The feed is a read-optimised projection and should be read through Day 12's
Dapper split rather than the write model. This is also where the fan-out-on-write
limit gets written down as an operational threshold rather than a paragraph:
one feed row per follower is right at hundreds and wrong at millions, and the
standard fix is excluding popular curators from fan-out and merging them at read
time. Not built — but the handler sits behind an interface, so it stays cheap.
*Done when* the feed endpoint reads through the projection and a load test
records where the write fan-out starts to hurt.

**Day 6 — Deploy it on the Day 23–26 infrastructure.**
The capstone becomes a target of the existing Bicep modules and the azd
deployment stack rather than something that runs only on a laptop:
`infra/modules/servicebus.bicep` already provisions the topic and subscriptions,
`infra/modules/sql.bicep` the database, `infra/modules/monitoring.bicep` the
Application Insights component. Day 26's KQL is what proves the async flow works
in the deployed system — the distributed trace stitching publish to fan-out
across the relay hop is the single most useful query this design can produce.
*Done when* a publish against the deployed API shows up as one operation
spanning both processes in the trace, and the error-rate alert covers the relay.

**Deliberately not in the plan.** Collection versioning, which is what
eventually replaces the freeze-on-publish rule. It is the right answer and it is
a larger piece of domain design than any of the six days above, so scheduling it
inside them would guarantee it arrives half-done. It gets its own ADR when it is
taken up.

---

### What did you learn this session?

That writing the ADR is what found the bug. The design document and the code had
both been read many times and the contradiction survived every reading, because
reading them separately never puts the claim and the counter-example on the same
page. Writing down *why* the outbox was the right choice meant stating the two
properties it buys — publish latency independent of follower count, and a
publish that does not fail when the subscriber is down — and once those were
written as a promise, the endpoint that broke both of them was impossible to
miss. An ADR is not documentation of a decision already made. It is the first
place the decision gets tested against the code.

The second thing: a comment admitting a shortcut is worth less than no comment
at all, because it converts a bug into a known-and-accepted design. "Draining
inline is the scaffold's shortcut, not the design" reads as diligence and
functions as permission. The same standard the repository already applies to
module boundaries — a rule is a test, not a convention — applies to every other
boundary it claims to have.

### What would break this?

The destructive outbox drain, first and worst, and it is now more dangerous than
it was before this change rather than less. Moving delivery into the background
removed the one thing that made a lost message noticeable: the 500 the curator
used to receive. A message whose handler throws is silently dequeued and never
retried, so the failure mode is a follower whose feed is permanently missing one
collection, with nothing anywhere recording that it should have been there.
Day 2 of the plan is the fix, and until it lands the scaffold demonstrates
at-least-once without providing it.

Second, the poll interval is a fixed 250 milliseconds against an in-memory
queue, which is free. Against a real table it is a query every 250 milliseconds
per instance, forever, mostly returning nothing — and multiplied by however many
API replicas Day 24's scaling settings permit, each of them racing for the same
unsent rows. The real relay is one process precisely so that this is not a
problem; a background loop inside the API is the shape that stops working the
moment the API scales out, which is an argument for day 3 happening on schedule
rather than being deferred as an optimisation.
