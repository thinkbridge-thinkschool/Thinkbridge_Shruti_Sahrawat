[← Back to full README](../../README.md)

## Day 20 — The transactional outbox

Write the domain change and an outbox row in one EF transaction, then a
separate relay publishes the row and marks it sent. Prove no message is lost
if the publish step crashes.

**Day 19 left this exact gap open on purpose.** Its README said so directly:
"the publisher is not wired into QuotesApi... wiring it in properly needs the
outbox pattern anyway - publishing inside the same transaction as the quote
insert, rather than after it, so a crash between the two cannot leave a quote
that no consumer ever hears about." Day 20 is that follow-through, not a new
problem. `POST /api/quotes` still creates the quote itself; the only change is
that it now also leaves a durable trail of what happened, for something else
to act on.

**Why a database write and a queue publish cannot share a transaction.**
They are two different systems. Nothing makes a network call to Service Bus
part of the same commit as a SQL insert - not a distributed transaction
coordinator, not a two-phase commit, nothing this stack has available. What
the database *can* make atomic with the insert is another row, in the same
database, on the same connection: a row describing the event. So the outbox
moves the hard problem from "make two different systems agree" to "read a
table on a schedule and retry until it is empty", which is a problem retries
can actually solve.

**The write side:**
[`QuoteRepository.AddAsync`](../../QuotesApi/Repositories/QuoteRepository.cs)
wraps the quote insert and the outbox insert in one explicit
`BeginTransactionAsync`/`CommitAsync`, not one `SaveChanges` call. That is a
deliberate difference from Day 19's `QuoteEventDispatcher`, which gets away
with a single SaveChanges because its ledger row needs nothing the projection
write doesn't already have. Here the outbox payload needs `quote.Id`, and that
value does not exist until the insert has actually executed against the
database - it cannot be included in a row staged before that call. The
transaction, not the call count, is what makes the two writes atomic: if the
second SaveChanges throws for any reason, the transaction is disposed without
a commit and the first insert rolls back with it.
[`AddAsync_WhenTheOutboxInsertFails_RollsBackTheQuoteToo`](../../Quotes.Tests.Unit/OutboxWriteTests.cs)
forces exactly that - a pre-seeded row collides with the outbox insert's
unique `MessageId` index, and the test checks the quote table from a separate
connection afterwards to confirm nothing partially landed.
`DeleteAsync` has no such problem, since the id it needs already exists, so
one call covers both writes there.

**The outbox table is
[`OutboxMessage`](../../QuotesApi/Models/OutboxMessage.cs):** an id, the
deterministic message id (Day 19's `QuoteEventIds`, unchanged - the relay has
to publish with the exact id the consumers' ledger already expects), the
event type, the JSON payload, when it happened, and when it was sent. A
unique index on the message id is what makes the atomicity test above
checkable rather than assumed, and it is the same reason a genuine retry of
the same event can never produce two outbox rows.

**The relay is a separate project,
[`Quotes.Outbox`](../../Quotes.Outbox/), not a background service inside
QuotesApi.** That mirrors Day 19's own reasoning for keeping the publisher out
of the API host: QuotesApi is deployed and serving real data, and a
`BackgroundService` that needs a live Service Bus namespace to start cleanly
would put that live application one missing resource away from failing to
start. `Quotes.Outbox` reads the same `OutboxMessages` table QuotesApi writes
to - via [`OutboxDbContext`](../../Quotes.Outbox/OutboxDbContext.cs), a
narrower view that knows about exactly one table and nothing else in
QuotesApi's schema - and owns no schema of its own: it never calls
`EnsureCreated` or `Migrate`, only a startup probe that fails loudly if the
table isn't there yet, because a relay that "started successfully" against a
database with no outbox table would sit in its poll loop forever looking
healthy while draining nothing.

**The crash scenario, and why it does not lose (or wrongly duplicate) a
message.**
[`OutboxRelay.RelayBatchAsync`](../../Quotes.Outbox/OutboxRelay.cs) publishes
a row, and only then marks it sent with its own SaveChanges. Between those two
steps there is no atomic way to make both happen or neither - the process can
die after Service Bus has already accepted the message and before the disk
write recording that lands. The proof is
[`RelayBatchAsync_WhenTheProcessDiesAfterPublishingButBeforeMarkingSent_TheRowSurvivesForRetry`](../../Quotes.Tests.Unit/OutboxRelayTests.cs):
a `FakeQuoteEventPublisher` records the send - so the test can tell "the
broker got it" apart from "the broker never saw this" - and *then* throws,
modelling the crash. The row is checked from a separate `OutboxDbContext`
afterwards and is still unsent, exactly where the next poll will find it.
Running the relay again, against a publisher that now succeeds, republishes
with the byte-identical message id and marks the row sent. Nothing was lost;
the broker did see one event published twice, never two different events -
and that duplicate is exactly the case Day 19 already built for: a consumer
keyed on `(MessageId, Consumer)` does the work once no matter how many times
the same id arrives.
[`QuoteEventDispatcherTests`](../../Quotes.Tests.Unit/QuoteEventDispatcherTests.cs)
is where that other half of the guarantee already lives - this exercise did
not have to re-prove it, only rely on it.

A second test,
[`RelayBatchAsync_WhenOneRowFailsToPublish_StillPublishesTheOthersInTheBatch`](../../Quotes.Tests.Unit/OutboxRelayTests.cs),
checks the batch doesn't fail closed: one row's publish failing leaves that
row unsent and every other row in the same poll unaffected, so a single bad
event cannot stall everything behind it in the table.

**What this deliberately does not do.**

Only one relay instance is assumed. Two running at once would both pick up
the same unsent row and both publish it - safe, for the same idempotency
reason above, but wasted work. Making that safe *and* efficient needs the row
claimed before publishing (an `UPDATE ... WHERE SentAt IS NULL` affecting
exactly one writer), the same shape as Day 19's ledger insert, and is out of
scope here.

The outbox table grows without bound, the same limitation Day 19's ledger
already documented for itself. Nothing here prunes sent rows; a real
deployment needs a retention job once rows are old enough that no relay could
still be mid-retry on them.

Ordering is not preserved across a batch. A row whose publish fails does not
block the rows after it, so two events for the same quote could reach the
broker out of order if one of them needed a retry. Nothing here depends on
order - Day 19 already noted competing consumers destroy it too - but a
workflow that did would need more than this relay provides.

**Verification status.** `dotnet test Quotes.Tests.Unit` and
`dotnet test Quotes.Tests.Integration` both ran clean locally: 194/194 unit
tests and 54/54 integration tests, including the new
`OutboxWriteTests`/`OutboxRelayTests` above and the `OutboxMessages`
migration applying against real SQL Server exactly as Day 19's tables did.
CI run #85 on commit `563b311` is green on `main`.

A review pass after that first green run found one real bug in
`OutboxRelay.RelayBatchAsync`: all rows in a batch share one
`OutboxDbContext`, so a row whose own `SaveChangesAsync` threw stayed tracked
as `Modified` and could ride along on - or drag down - a later row's
successful save in the same batch, even though it was reported here as
`Failed`. Fixed by resetting that row's entry to `Unchanged` in the catch
block; `RelayBatchAsync_WhenOneRowFailsToPublish_StillPublishesTheOthersInTheBatch`
was also tightened to read its verification back from a fresh
`OutboxDbContext` rather than the same tracked one the relay had just used,
so a regression here fails the test again instead of passing by reading the
in-memory tracker's optimistic view.

**One deployment gap this exercise did not close.** `azure.yaml` sets
`Database:Provider=SqlServer` but nothing sets `Database:SchemaBootstrap`,
so `Program.cs` defaults Azure SQL to `EnsureCreated()` - and
`EnsureCreated()` is a no-op against a database that already has tables,
which this one does from Day 19. Deployed as-is, `OutboxMessages` is never
created on the live database, and the first `POST /api/quotes` after deploy
would 500 on the outbox insert. See `sql/add-outbox-table.sql` and the note
in `DEPLOY-RUNBOOK.md` for the manual step this needs before the next
deploy; a real fix would carry SQL Server migrations inside `QuotesApi`
itself and switch Azure SQL to `Migrate()`, which is out of scope for what
this exercise asked for.
