# Postmortem — 31 days, one capstone

Written on day 32, about the whole thing. One page, three questions, no
rounding up.

## What shipped

A quotes API on Azure Container Apps behind a Static Web App front door, with
JWT auth, rate limiting, a CQRS read side, Redis-backed caching, Polly
resilience, Service Bus messaging with a dead-letter path, and an outbox — all
of it deployed, and most of it tested. Alongside it, a modular-monolith capstone
with a real domain, EF Core persistence, a transactional outbox with a
non-destructive drain, architecture tests that fail the build when a module
reaches into another, and 46 tests across four layers at 96% line coverage.

What did not ship: the capstone was never deployed. Three of the six days its
own build plan set out — the Service Bus relay, persistence for Sharing and
Catalog, the read side — were not built. Two of its three modules still keep
their state in process memory. That is written up properly in
[STATUS.md](STATUS.md) rather than softened here.

## What I would do differently

**Make security structural instead of remembered.** On day 32 I found
`CollectionsController` had no authorization at all — seven endpoints,
including a DELETE and a GET that returns every collection, reachable
anonymously through the live front door. The quotes endpoints were fine,
because day 25 put `RequireAuthorization()` on the *group*, so anything added
to that group is protected by default. The controller was simply not in that
group and inherited none of it. One `AddAuthorization` fallback policy at
startup would have made the omission impossible rather than merely unlikely.
Day 27's security pass looked at endpoint groups and day 31's re-check looked at
the capstone; neither looked at the one file that was registered a different
way.

The same shape shows up twice more: the capstone's `curatorId` and this
controller's `ownerId` both take identity from the caller's own request body and
then check it against itself. Three instances of one mistake is not three
mistakes, it is a missing default.

**Measure the middle, not just the before and the after.** Day 31's performance
work would have read as a complete fix if I had only run it twice. The run in
between — the same load with the relay switched off, before changing any code —
showed that p95 was almost entirely the relay and p99 almost entirely was not.
The fix took p95 from 153.73ms to 8.50ms and left p99 at 465ms, which is the
floor set by something I had not touched. Two data points would have let me
claim the win and miss the ceiling.

**Keep one list per fact.** Three separate hand-maintained lists of projects —
the CI matrix, the solution file, the coverage filter — produced three separate
misses in a month, including five tests that ran on a laptop and nowhere else
while CI reported green. I still think the lists should be explicit rather than
globbed, for the same reason `ModuleBoundaries.Allowed` is a table. But
explicit and *unlinked* is what cost the time.

**Write the deployment down as it is, not as it was.** The runbook in this repo
spent weeks pointing at a SQL server and a Container App in a subscription that
no longer existed. Anyone following it got a connection failure and no
explanation.

## What the hardest bug taught me

Day 30, adding the outbox table: four of five new tests failed with
`NotSupportedException` — SQLite will not translate `ORDER BY` over a
`DateTimeOffset`. The useful part was the fifth test. It passed, and it was the
only one whose query had no `ORDER BY` in it, which located the cause before I
had changed anything.

That reframed what tests are for. I had been treating them as a safety net —
they tell you something broke. The pattern of which ones broke is a different
and better tool: it tells you *where*. That only works if the tests are small
enough to differ from each other, which is an argument for granularity that has
nothing to do with coverage.

The bug itself taught something narrower and worth keeping: a model both
providers accept at migration time can still be a model only one of them
accepts at query time. It compiles, it migrates, it passes review, and it fails
on the first request. Day 24 had already found the same class of problem with a
migration generated against the wrong provider, and I did not connect them until
the second one.

## The one thing I am proudest of

Not the outbox, though it is the best code here. It is that the write-ups say
what actually happened, including the four days where what happened was that I
was wrong.

Day 31 records that I predicted the unbounded outbox read would be a latency
problem and measured a payload problem instead. Day 30 records that I wrote two
numbers into a README that were never on screen, and what I changed so it could
not happen again. Day 31's performance table carries a row whose only purpose is
to show that the fix was partial. A test in this repo is named
`Collections_StillLetOneSignedInUserSeeAnothersData_KnownGap`, and it asserts a
hole is still open so that closing it is a failing test rather than a surprise.

The version of this project where every document claims success would have been
easier to write and worth less. Roughly a third of what I can now explain in an
interview, I can explain because it is written down as a mistake with a number
attached.
