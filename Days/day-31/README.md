[← Back to the day index](../README.md)

## Day 31 — Polish: tests, perf, security

Tests at every layer (unit, integration via WebApplicationFactory, one E2E), a
perf pass (the p99 of your hottest path), and a security re-check. Green CI
gate.

Nothing in this day adds a feature. What it adds is the ability to notice that
a feature stopped working — at the layer a user meets it, under load, and in
the dependency tree.

---

### What changed

| File | What it does now |
|---|---|
| `tests/Capstone.Api.Tests/` | New project. 15 tests over real HTTP against a real database |
| `tests/Capstone.Api.Tests/CapstoneApiFactory.cs` | `WebApplicationFactory<Program>`, one SQLite file per test, migrations applied |
| `tests/Capstone.Api.Tests/CollectionEndpointsTests.cs` | 11 tests: status codes, the DomainException mapping, the information-disclosure property |
| `tests/Capstone.Api.Tests/MigrationTests.cs` | 3 tests: the migrations build the schema the model expects |
| `tests/Capstone.Api.Tests/EndToEndPublishTests.cs` | 1 test, every boundary at once: publish → outbox → relay → feed |
| `Capstone.Api/Program.cs` | `public partial class Program` so a test host can find the entry point |
| `Capstone.Api/RelayHostedService.cs` | Poll interval reads `Relay:PollIntervalMilliseconds` |
| `Capstone.Api/InProcessRelay.cs` | Acknowledges the batch once, in a `finally`, instead of once per row |
| `Outbox/IOutboxStore.cs` | `MarkSentAsync` takes a set of ids |
| `Outbox/EfOutboxStore.cs` | One `ExecuteUpdateAsync` per drain instead of two round trips per row |
| `Outbox/OutboxMessage.cs` | `MarkSent` and `ToRecord` deleted — nothing called either |
| `capstone/coverlet.runsettings` | New. The capstone's own coverage filter |
| `capstone/perf/` | New. k6 script and the measurement write-up |
| `scripts/check-coverage.py` | `--by-project`, so coverage can be read per layer |
| `.github/workflows/ci.yml` | `Capstone.Api.Tests` in the matrix; capstone coverage collected and gated at 90% |
| `Thinkbridge_Shruti_Sahrawat.slnx` | Two test projects that were missing from the solution |

---

### The pyramid, and the rule that keeps each test in its layer

| Layer | Project | Tests | What only this layer can answer |
|---|---|---:|---|
| Unit | `Capstone.Curation.Domain.Tests` | 20 | Whether a rule is right |
| Architecture | `Capstone.ArchitectureTests` | 6 | Whether a module reached into another module |
| Integration (infrastructure) | `Capstone.Curation.Infrastructure.Tests` | 5 | Whether one `SaveChanges` really commits two tables together |
| Integration (HTTP) | `Capstone.Api.Tests` | 14 | Whether the wiring, the status codes and the error mapping are right |
| End-to-end | `Capstone.Api.Tests` | 1 | Whether the seams between all of the above actually meet |

The rule is enforced by project references rather than by discipline.
`Capstone.Curation.Domain.Tests` references exactly one project — the domain —
so it *cannot* boot a host or open a database, and every test in it is
necessarily about a rule. `Capstone.Api.Tests` references the API and the
infrastructure and deliberately not a module's Application or Domain assembly,
so every test in it can only say what an HTTP client could say. A test that
reached into `Collection` to assert an invariant would be a domain test paying
the price of a web host, and here it would not compile.

Every rule asserted in `CollectionEndpointsTests` is *also* asserted in the
domain suite, and that duplication is the point rather than waste. The domain
suite proves that publishing an empty collection is refused. The endpoint suite
proves that the refusal arrives at a caller as a 400 carrying the domain's own
sentence rather than as a 500. Those are different claims, and the second one
has been broken by a one-line change to a middleware before.

**One end-to-end test, on purpose.** It is the only test that crosses HTTP, the
aggregate, one transaction, a table, a background thread, a subscriber in
another module, and back out through HTTP. That is what makes it worth having
and also what makes it expensive to own: it is the slowest test here and the
only one whose failure does not tell you where the problem is. A suite of these
is a suite that takes ten minutes to say something went wrong somewhere.

It also has to *wait*, and the waiting is the assertion. Publish returns once
the collection and the outbox row are committed, deliberately before the
fan-out, so "the feed contains it" is never true at an instant the test
controls — it becomes true. Polling with a deadline is the honest shape for
that. A fixed sleep is either flaky or slow and usually both, and draining the
relay by hand from the test would assert against a delivery path that is not
the one running in production.

---

### Coverage at each layer

Merged across the three suites that execute application code, using
`capstone/coverlet.runsettings`:

```
$ python scripts\check-coverage.py --by-project 0 @reports

Reports merged:  3
Files:           17
Lines covered:   453 / 469
Line coverage:   96.59%

Coverage by project:
  project                           covered  total     rate
  Capstone.Api                          149    156   95.51%
  Capstone.Catalog.Infrastructure         2      7   28.57%
  Capstone.Curation.Application          14     14  100.00%
  Capstone.Curation.Domain               61     64   95.31%
  Capstone.Curation.Infrastructure      198    199   99.50%
  Capstone.SharedKernel                   6      6  100.00%
  Capstone.Sharing.Application            9      9  100.00%
  Capstone.Sharing.Infrastructure        14     14  100.00%
```

**96.59% is the number that hides the finding.** `Capstone.Catalog.Infrastructure`
is at 28.57% — five of its seven lines never execute. Catalog is the one module
nothing in the pyramid reaches: it has no test project of its own, and the only
thing that touches it is `FindMissingAsync` on the publish happy path. It is
also the module a real integration would replace first, since it is a stand-in
for the existing quote tables. So the module with the least evidence attached to
it is the one most likely to change, and a single merged percentage says none of
that.

The gap is not closed by writing a test for a seeded dictionary. It is recorded
because "the scaffold module is untested" is a true and useful statement, and
"96.59%" is a true and useless one.

`--by-project` was added to `scripts/check-coverage.py` for exactly this. It
changes output only — the gate is still the merged figure, because a per-project
threshold fails a project the day it is created and teaches people to write a
token test rather than a useful one.

---

### The perf pass

Full measurement, method and raw numbers: [`capstone/perf/README.md`](../../capstone/perf/README.md).

Hot path: `POST /api/collections/{id}/publish`. 10 virtual users, 60 seconds,
20 followers per curator, Release build, SQLite.

| | p50 | p90 | p95 | p99 | max | iter/s | delivered in 60s |
|---|---:|---:|---:|---:|---:|---:|---:|
| **Before** | 2.00 ms | 5.29 ms | **153.73 ms** | **614.24 ms** | 3.55 s | 146 | 1,576 of 8,865 |
| Relay parked *(diagnostic)* | 1.99 ms | 4.03 ms | 7.80 ms | 463.25 ms | 2.16 s | 171 | 0 of 10,421 |
| **After** | 1.76 ms | 3.87 ms | **8.50 ms** | **465.46 ms** | 2.17 s | 170 | 3,126 of 10,278 |

The middle row is the part worth keeping. It is not a fix — it is the same load
with the relay asleep, run *before* changing anything, to find out how much of
the tail belonged to the relay at all. p95 collapsed 20× with no code touched,
and p99 moved by only a quarter. Without it, the after-numbers read as a
complete fix. They are not one: p99's floor is the API contending with itself
for SQLite's single writer, three writes per iteration, and this change does not
address that.

That diagnostic was free because the poll interval had become configuration
earlier the same morning — for the test host's benefit, so an endpoint test could
park the relay and assert a row was still unsent without racing a background
thread. A testability change turned into the measurement instrument.

---

### Findings

**1 — A red test whose obvious fix would have damaged the code.**
`Publishing_a_collection_owned_by_someone_else_is_answered_as_if_it_did_not_exist`
failed on first run:

```
Expected (mallory.ErrorMessageAsync()) to be a match with the expectation
because a collection you do not own must be indistinguishable from one that is
not there, but it differs at index 11:
      ↓ (actual)
  "…01a0ad89-6873-769e-9fbb-ce68ad90bade was not found."
  "…5f484e11-5818-4bfb-9aa9-4467766cd60f was not found."
      ↑ (expected)
```

Index 11 is the first character after `Collection `. The two messages differ
only in the id each one echoes back, and the ids differ because they *are*
different ids — the caller sent them. The property worth asserting is "the
response tells you nothing you did not already send"; what was asserted was
"the two strings are byte-identical", which is a stronger claim that cannot
ever hold.

Taking the red test at face value, the fix is to strip the id out of the
message. The test goes green and every not-found response in the API gets less
useful. The assertion now redacts the caller's own id from both messages and
compares what is left, which is the property stated properly:

```csharp
Redact(refusedToMallory, real)
    .Should().Be(Redact(neverExisted, imaginary));
refusedToMallory.Should().Be($"Collection {real} was not found.");
```

**2 — Twenty write transactions per drain were the entire p95 tail.**
`MarkSentAsync` took one id and cost two round trips: a `FirstOrDefaultAsync`,
a guard, a `SaveChangesAsync`. The relay called it per delivered message, so a
twenty-row batch took SQLite's single write lock twenty times, four times a
second, alongside every request. p95 on the publish endpoint was **153.73 ms**
with the relay running and **7.80 ms** with it parked.

One `ExecuteUpdateAsync` over the whole batch, in a `finally` so the
deliver-then-acknowledge ordering is untouched, brings p95 to **8.50 ms** —
within a millisecond of the relay not running at all.

What it trades away, recorded because an unwritten redelivery window is a
duplicate feed entry nobody can explain: a process that dies mid-batch used to
have acknowledged what it had already delivered, and now has not, so the whole
batch redelivers. Still at-least-once, still absorbed by the same
`(MessageId, consumer)` dedup that was always load-bearing, and wider than it
was.

**3 — The relay cannot keep up, and making it cheaper did not change that.**
Delivery went from 1,576 to 3,126 messages in sixty seconds. That is 52/s
against a publish rate of 170/s, and **7,152 rows were still unsent when the
load stopped.**

`BatchSize` 20 every 250 ms caps the relay at 80 messages a second no matter
what else is true. The acknowledge cost is gone; the sleep is not. Above that
rate the outbox stops being a buffer that absorbs a burst and becomes a queue
that only drains once the burst ends.

Raising the batch size or shortening the interval moves the number without
changing the shape, and the shape is the problem — this is
[Day 30](../day-30/README.md)'s finding 2 arriving with a figure attached. Day 3
of the build plan replaces the table poll with a broker that is pushed to.
Left as a measured ceiling rather than tuned into looking fine.

**4 — The unbounded outbox read is a payload problem, not a latency one, and I
predicted the opposite.** `GET /api/outbox` reads the whole table with no
`Take`. Nothing prunes the table. The obvious conclusion is that it degrades
linearly, and the measurement says otherwise — same request, same code:

| rows | time | body |
|---:|---:|---:|
| 0 | 22.5 ms | 2 bytes |
| 10,278 | 45.0 ms | 1,952,634 bytes |

The body grew by a factor of a million and the time doubled. The defect is
real and still needs a bound — 10,278 rows is sixty seconds of one load test —
but it would have gone into this document as a latency bug if it had been
reasoned about instead of measured.

**5 — Two test projects were in CI and not in the solution.**
`Capstone.Curation.Infrastructure.Tests` was added on Day 30, added to
`ci.yml`, and never added to `Thinkbridge_Shruti_Sahrawat.slnx`. So `dotnet
build` at the repository root had not been building the five outbox tests
since the day they were written, and neither would anyone opening the solution
in an IDE.

That is the third time this month a project was added in one place and not
another — Day 30's CI matrix, the solution file, and the same file again for
today's project. The pattern is the finding, not the individual misses: every
list of projects in this repository is hand-maintained, and there are now three
of them. The position that keeps them explicit is defended in
[Day 30](../day-30/README.md)'s review section and still holds, but the cost is
now measurable at three incidents.

**6 — Two methods nothing called, found by the coverage report rather than by
reading.** `OutboxMessage.MarkSent` died when the acknowledge became a bulk
`UPDATE`. `OutboxMessage.ToRecord` had already been dead — the read path
projects straight into `OutboxRecord` inside the query, precisely so the relay
never holds a tracked entity.

Deleting both took `Capstone.Curation.Infrastructure` from 98.49% to **99.50%**,
and both files dropped off the uncovered list entirely — which is what confirmed
those were the missing lines. Worth being clear-eyed about: nothing got better
tested. The denominator shrank. A coverage number that rises with no new test is
a thing to be suspicious of, including when it is your own.

**7 — A failed startup takes the relay down noisily, and the log is what
fails.** Observed while the port was still held by a previous run. The host
failed to bind, disposal began, and `RelayHostedService` — already running —
resolved a scope from a disposed provider:

```
fail: Capstone.Api.RelayHostedService[0]
      Outbox drain failed; the unsent rows will be retried on the next poll.
      System.ObjectDisposedException: Cannot access a disposed object.
      Object name: 'IServiceProvider'.
fail: Microsoft.Extensions.Hosting.Internal.Host[9]
      BackgroundService failed
      System.AggregateException: An error occurred while writing to logger(s).
      (Cannot access a disposed object. Object name: 'EventLogInternal'.)
```

The catch-all did its job and then the logger it called threw, because the
event-log sink was disposed too. Only reachable when startup fails, so it
affects nothing that runs; recorded rather than fixed, because the fix —
checking `stoppingToken` before logging — is a change to shutdown behaviour
that deserves its own test and this day already has its change.

---

### The security re-check

Re-running [Day 27](../day-27/README.md)'s checklist against the capstone API
rather than against QuotesApi, since the capstone is the thing that grew.

**S1 — There is no authentication, and the ownership check compares the
caller's input to the caller's input.** This is the real finding.
`PublishCollectionHandler` does verify that the curator owns the collection:

```csharp
if (collection.CuratorId != curator)
{
    throw new DomainException($"Collection {id} was not found.");
}
```

Both sides of that comparison originate in the request body. `curatorId` on
`POST /api/collections` decides who owns a collection, and `curatorId` on
`POST /publish` claims to be them. So anyone can create a collection as any
curator and publish it to that curator's followers, with no credential of any
kind. Demonstrated below.

QuotesApi solved this on [Day 25](../day-25/README.md) — JWT, claims,
`RequireAuthorization()` at group level as the secure-by-default shape. The
capstone has none of it, and the honest description is that its authorisation
check is currently theatre: correct logic over an untrusted identity.

Not fixed today, and the reason is scope rather than difficulty. Adding
authentication changes every endpoint, every one of the 15 new tests, the
walkthrough, and the load script. The brief for today is a security re-check,
and the output of a re-check is a finding with evidence.

**S2 — CVE-2025-6965, High, and genuinely unpatchable right now.**

```
Project `Capstone.Api` has the following vulnerable packages
   [net10.0]:
   Transitive Package             Resolved   Severity   Advisory URL
   > SQLitePCLRaw.lib.e_sqlite3   2.1.11     High       GHSA-2m69-gcr7-jv3q
```

The flaw is in SQLite itself below 3.50.2 — memory corruption where the number
of aggregate terms can exceed the number of available columns. SQLite shipped a
fix; **SQLitePCLRaw has published no patched version**, so there is nothing to
upgrade to. It arrives through `Microsoft.EntityFrameworkCore.Sqlite` and is
tracked upstream on dotnet/efcore as
[#38463](https://github.com/dotnet/efcore/issues/38463),
[#38467](https://github.com/dotnet/efcore/issues/38467) and
[#38547](https://github.com/dotnet/efcore/issues/38547).

Reachability, which is the part worth stating rather than the severity: getting
at this needs attacker-controlled SQL, and nothing here composes SQL from input
— every query is EF LINQ with parameters. The deployed shape does not use
SQLite at all, since `infra/modules/sql.bicep` provisions Azure SQL and
`Database:Provider` selects it. The vulnerable native library still ships in the
image, which is the part that stays true regardless.

**S3 — `/api/outbox` is an unauthenticated activity log.** It returns every
message id, event type and timestamp ever staged, for every curator, to anyone.
It does *not* return `Payload`, which is a deliberate projection and the right
one — but the timeline alone tells an anonymous caller exactly when each curator
published and how often. `Program.cs` already says this endpoint belongs behind
[Day 27](../day-27/README.md)'s diagnostics gate; finding 4 adds that it should
be bounded as well as gated.

**Checked and clean.** Three things that could have been findings and are not:

- No database file has ever been committed. `.gitignore` covers `*.db`,
  `*.db-wal` and `*.db-shm`, and `git ls-files` confirms nothing slipped in
  before the rule did — those are different claims and only the second one
  matters.
- A collection you do not own and a collection that does not exist return the
  same status and the same message modulo the id you supplied. Asserted, not
  assumed: finding 1 is the story of getting that assertion right.
- `SSH.NET` 2024.2.0 carries GHSA-q939-rpr3-3284 in `Quotes.Tests.Integration`.
  It arrives through Testcontainers, is test-only, and ships in nothing.

---

### Proof

Every suite, green:

```
$ dotnet test capstone\tests\Capstone.Curation.Domain.Tests --configuration Release
Test summary: total: 20, failed: 0, succeeded: 20, skipped: 0, duration: 2.6s

$ dotnet test capstone\tests\Capstone.Curation.Infrastructure.Tests --configuration Release
Test summary: total: 5, failed: 0, succeeded: 5, skipped: 0, duration: 6.4s

$ dotnet test capstone\tests\Capstone.Api.Tests --configuration Release
Test summary: total: 15, failed: 0, succeeded: 15, skipped: 0, duration: 7.0s
```

The end-to-end test is the one that would catch a wiring mistake nothing else
can see — a relay registered as a singleton holding a scoped `DbContext`, a
hosted service that never starts, an integration event whose JSON does not
round-trip, a subscriber bound to the wrong follower directory instance. All
four are composition-root mistakes, and a composition root is made of exactly
those.

**S1, demonstrated.** No token, no cookie, no header of any kind — a stranger
creating and publishing a collection as `alice`, and it reaching `bob`:

```
POST /api/collections   {"curatorId":"alice","name":"Posted by a stranger"}
{ "collectionId": "01a0adb4-1bf6-775d-96f4-c00a7643112a" }

POST /api/collections/01a0adb4.../items     {"quoteId":1}
{ "items": 1 }

POST /api/collections/01a0adb4.../publish   {"curatorId":"alice"}
{ "published": true }

GET /api/feed/bob
[ { "collectionId": "01a0adb4-1bf6-775d-96f4-c00a7643112a",
    "curatorId":    "alice",
    "name":         "Posted by a stranger",
    "publishedAt":  "2026-09-17T04:50:57.5988578+00:00" } ]

GET /api/outbox
[ { "messageId":  "01a0adb4-1dc1-70e9-ac8c-431ffcaeb4f1",
    "eventType":  "curation.collection.published.v1",
    "occurredAt": "2026-09-17T04:50:57.5988578+00:00",
    "sentAt":     "2026-09-17T04:50:57.7473435+00:00",
    "delivered":  true } ]
```

Every response captured with `| ConvertTo-Json`, because
[Day 29](../day-29/README.md) learned the hard way that PowerShell renders a
sequence of differently-shaped objects under the first one's table header and
shows the rest as blank rows — which is how values that were never on screen
once made it into a README.

That transcript also times the happy path end to end: `occurredAt
04:50:57.5988578` to `sentAt 04:50:57.7473435` is **148.5 ms** from commit to
acknowledged on an idle system. One poll interval plus scheduling.

---

### What did you learn this session?

A merged coverage percentage and a p99 both average away the thing you needed
to see — 96.59% hid a module at 28.57%, and the p99 hid the fact that three
quarters of the tail was never the component being fixed.

### What would break this?

Publishing faster than 80 messages a second: the relay's batch-per-poll ceiling
means the outbox stops absorbing the burst and just accumulates it, and nothing
alerts on a backlog that is growing.

### GitHub link

[`Days/day-31/`](https://github.com/thinkbridge-thinkschool/Thinkbridge_Shruti_Sahrawat/tree/main/Days/day-31)
