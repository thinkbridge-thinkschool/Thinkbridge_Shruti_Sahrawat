[← ADR index](README.md) · [← Capstone design](../../capstone/README.md)

# ADR-0001 — Capstone modules are assemblies, and the dependency graph is a test

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 14 September 2026 |
| **Scope** | `capstone/` — the Curation, Catalog and Sharing modules |
| **Decided by** | Shruti Sahrawat, for mentor review |
| **Supersedes** | Nothing. First record. |

---

## Context

The capstone builds one user-visible slice: a curator assembles a collection of
quotes, publishes it, and their followers see it appear in a feed. That slice
spans three bounded contexts — Curation owns the collection and its publish
lifecycle, Catalog owns quotes, Sharing owns follows and feeds.

The question this record answers is not which pattern to use inside any one of
them. It is what stops the three from quietly becoming one.

That question is not hypothetical here, because this repository already contains
the counter-example. `QuotesApi` is layered by folder inside a single assembly:
there is a `Domain` folder, a `Data` folder, a `Services` folder, and nothing
whatsoever prevents a type in `Domain` from referencing `QuotesDbContext`. The
layering holds because no one has broken it, which makes it a social property of
the team rather than a structural property of the code. It survives one careful
developer and does not survive a deadline.

Days 19 through 22 built the machinery the capstone's publish flow needs — the
transactional outbox, a Service Bus topic with dead-lettering, a relay, and a
Polly resilience pipeline. Those pieces work. What the repository does not have
is a structure that keeps them from growing into each other, and adding a fourth
working piece to an unstructured pile does not make the pile better.

A second constraint shapes the answer. This is a learning capstone with a
single slice and a scaffolded persistence layer, built by one person on a
schedule. Whatever is chosen has to be affordable now and has to leave the door
open to extraction later, because "we will split it into services when we need
to" is only true if the seams exist before the day someone needs them.

---

## Decision

**Every module layer is a separate .NET assembly. A module may reference another
module only through that module's `Contracts` project. The complete permitted
dependency graph is written down once in code, and two tests fail the build if
the real graph departs from it.**

The graph lives in
[`ModuleBoundaries.Allowed`](../../capstone/tests/Capstone.ArchitectureTests/ModuleBoundaries.cs)
(lines 24–86) as a dictionary from assembly name to the capstone assemblies it
is permitted to reference. Anything not listed is a violation. The substance of
it:

| Assembly | May reference | Why that line exists |
|---|---|---|
| `Capstone.SharedKernel` | nothing | It is shared precisely because it depends on nothing. The moment it references a module, every module transitively depends on that one. |
| `*.Contracts` | nothing | Subscribing to Curation must not mean compiling against Curation's aggregate, or the boundary is decorative. |
| `Capstone.Curation.Domain` | shared kernel only | No EF, no ASP.NET, no sibling module. The core stays testable with no I/O. |
| `Capstone.Curation.Application` | own domain + other modules' **Contracts** | Use cases may cross a boundary, but only through published language. |
| `*.Infrastructure` | its own module | Adapters see inward, never sideways. |
| `Capstone.Sharing.Application` | `Capstone.Curation.Contracts` | This single entry is the entire coupling between Curation and Sharing. |
| `Capstone.Api` | everything | Somebody has to bolt the modules together, and it should be exactly one somebody. |

Two tests enforce it, and the pair is deliberate because either alone leaves a
real gap:

[`DeclaredReferenceTests`](../../capstone/tests/Capstone.ArchitectureTests/DeclaredReferenceTests.cs)
reads the `.csproj` files. It catches a reference that has been *declared* but
not yet used — which is exactly the state a violation is in on the day someone
adds it, before they write the code that needs it.

[`CompiledReferenceTests`](../../capstone/tests/Capstone.ArchitectureTests/CompiledReferenceTests.cs)
reads `GetReferencedAssemblies()` on the built output. It catches a dependency
that arrived by a route the project file does not obviously show — a transitive
reference a project has started binding to directly. It also forces each
assembly to load by touching one type from it (lines 30–41), because
`GetReferencedAssemblies()` on an assembly the runtime never loaded is a test
that passes by not looking.

The rule about *reading intent* versus *reading outcome* is the point: the
compiler drops an unused project reference from the emitted assembly, so a
reflection-only check is blind for precisely as long as the violation is
invisible in behaviour.

Both run on every push, as the `capstone` job in
[`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) (lines 78–86).

---

## Alternatives considered

### 1. Folders inside one assembly — what `QuotesApi` already does

The cheapest option, and the one with the strongest argument in its favour: it
is already in the repository, it is what most .NET tutorials show, and for a
codebase of this size a disciplined developer can hold the boundaries in their
head.

Rejected because a boundary that depends on someone remembering it is not a
boundary, it is an intention. There is no moment at which crossing it costs
anything, so the crossing happens under time pressure, is invisible in review,
and is discovered a year later when someone tries to extract Sharing and finds
it reaches into Curation's aggregate in eleven places. The failure mode of this
option is not that it breaks — it is that it decays silently and the bill
arrives all at once.

### 2. Assemblies, but no enforcement test

Better than folders: the compiler now refuses a *cycle*, and a wrong reference
is at least visible in a `.csproj` diff.

Rejected because the compiler only refuses cycles, and almost every boundary
violation that matters is not a cycle. `Capstone.Curation.Domain` referencing
`Capstone.Catalog.Contracts` compiles perfectly — it is a straight line, not a
loop — and it is forbidden here because the domain is meant to see the shared
kernel and nothing else. Relying on review to catch it puts the burden on the
reviewer to remember a rule that is written nowhere. Writing the rule down as a
table that fails the build moves the conversation to the moment someone tries
it, which is the only moment the conversation is cheap.

### 3. Three separately deployable services

The option that would make the boundaries physically unbreakable, and the one
the design is shaped to permit later.

Rejected as premature for this slice. It buys enforcement at the cost of
distributed transactions where a local commit would do, three deployment
pipelines, network failure modes between contexts that currently cannot fail,
and an integration test suite that needs three processes running before it can
assert anything about one aggregate's invariants. The capstone has one slice and
no independent scaling pressure — Sharing's fan-out is the only part with a
different scaling shape, and it is not yet under load. Paying distribution costs
now to buy an isolation this decision already provides in-process is the
textbook version of the mistake.

The relevant property is that this option stays available. Because Sharing
depends on exactly one thing — `Capstone.Curation.Contracts` — extracting it
later is a deployment change plus swapping the in-process relay for the Service
Bus one that Day 20 already built. The seam is where a service boundary would
go, so the migration does not require a redesign.

### 4. A third-party architecture-testing library — NetArchTest, ArchUnitNET

Rejected, but narrowly, and this is the alternative most likely to be raised.
Those libraries are more expressive than a hand-written dictionary: namespace
rules, naming conventions, layer helpers, and fluent assertions that read well.

Two reasons the hand-rolled version won here. First, both libraries work by
reflection over loaded assemblies, so both inherit the blind spot
`DeclaredReferenceTests` exists to cover — a declared-but-unused reference is
invisible to them, and that is the state every violation passes through.
Second, the table *is* the documentation: `ModuleBoundaries.Allowed` is forty
lines that a reviewer reads top to bottom to learn the architecture, with the
reasoning for each entry in a comment beside it. A fluent rule set expresses the
same constraint as a query and loses the place to explain why.

If the module count grows past roughly a dozen, the hand-rolled version stops
scaling and this decision should be reopened.

---

## The trade-off, stated plainly

**What is paid:** twelve projects for one feature slice. A cold build is slower.
A new type that two modules both need forces a decision about which module
publishes it and whether it deserves a `Contracts` project. Widening the graph
means editing a table and defending the edit, so the friction that stops
accidental coupling also slows down deliberate coupling.

**What is bought:** the boundaries are real rather than aspirational, they are
checked on every push rather than at review time, and the extraction path to
separate services is a deployment change rather than a rewrite.

**Why the trade is worth taking here:** the cost is paid once, at setup, in a
currency that does not compound — twelve projects is not twice as hard as six.
The cost of the rejected option compounds every week, is invisible while it
accrues, and comes due at the worst possible moment, which is the moment someone
finally needs the modules separated.

---

## Consequences

**Follows from this decision.** A module gets a `Contracts` project only when a
second module needs one, never from a template — Sharing has none, because
nothing consumes Sharing yet. The domain event `CollectionPublished` and the
integration event `CollectionPublishedIntegrationEvent` must be separate types,
because the first lives in `Curation.Domain` which Sharing may not reference;
[`DomainEventTranslator`](../../capstone/src/Modules/Curation/Capstone.Curation.Infrastructure/Outbox/DomainEventTranslator.cs)
is the single place that knows both, so refactoring the aggregate becomes a
compile error in one file instead of a silent breaking change for every
subscriber.

**Accepted weakness.** `Capstone.Api` is allowed to reference everything, which
makes the composition root a node with no restrictions at all. Nothing stops a
future endpoint there from constructing a `Collection` directly and bypassing
`PublishCollectionHandler` entirely. That is inherent — something has to know
all the parts — and it is contained rather than solved: the composition root is
two files, so the place where the rule does not apply is small enough to read.

**Cost already observed.** Wiring in `Program.cs` (lines 26–54) is verbose
because each module registers its own adapters and the blocks may not know about
each other. The intended fix — `AddCurationModule()` / `AddSharingModule()`
extension methods owned by each module — is noted in the file and deferred.

---

## Evidence this works

A rule that has only ever been green is a rule nobody has proven can fail. So
the boundary was broken on purpose: a reference from `Capstone.Curation.Domain`
to `Capstone.Catalog.Contracts` was added deliberately. It compiles cleanly,
because it is not a cycle and nothing uses it — which is exactly the case
`DeclaredReferenceTests` exists to catch and `CompiledReferenceTests` cannot
see.

Exactly one of the six tests failed, and it named the violating edge. The
reference was removed and the suite returned to 6 of 6 passing.

That is the difference between a dependency rule and a dependency diagram. This
one has been observed stopping something.

---

## When this should be reopened

If the module count passes roughly a dozen, the hand-maintained table becomes
the bottleneck and a rule-based library (alternative 4) wins on maintenance
cost. If Sharing develops genuinely independent scaling or deployment pressure
— the fan-out is the candidate — alternative 3 becomes the right answer, and
this decision is what makes taking it cheap. If a second team starts working in
a different module, the enforcement matters more, not less, and nothing here
changes.
