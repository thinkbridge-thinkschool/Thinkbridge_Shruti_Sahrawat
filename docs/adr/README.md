[← Back to the day index](../../Days/README.md)

# Architecture decision records

One file per decision that would be expensive to reverse and hard to
reconstruct from the code alone. A record explains the alternatives that were
rejected and why, because six months later the code shows only what was chosen
and every rejected option looks obvious in hindsight.

The format is Nygard's, kept deliberately short: context, decision,
alternatives, trade-off, consequences, and the conditions under which the
decision should be reopened. A record is immutable once accepted — a decision
that changes gets a new record that supersedes the old one, so the reasoning
that was true at the time stays readable.

| ADR | Decision | Status | Date |
|---|---|---|---|
| [0001](0001-modules-as-assemblies-enforced-by-tests.md) | Capstone modules are separate assemblies, and the permitted dependency graph is a test rather than a diagram | Accepted | 14 September 2026 |

Decisions recorded in day write-ups rather than here — the outbox (Day 20), the
Polly pipeline order (Day 22), subscription-scoped Bicep (Day 23) — stay there
because their reasoning is inseparable from the exercise that produced them.
This directory is for decisions about the capstone's shape, which no single day
owns.
