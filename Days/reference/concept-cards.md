[← Back to full README](../../README.md)

## Concept cards

Three cards were conceptual rather than build tasks. Where each one landed in the code:

**Day 1 — Tools check.** .NET SDK 10.0.302, Node 24 (runs `hello.ts` natively, no `tsc` step), Git, VS Code with C# Dev Kit, Copilot, Claude Code — the last used for the Day 1 refactor, the Day 2 rich-model rewrite, and three Day 3 test projects.

**Day 2 — Entity, value object, aggregate root.** Demonstrated in [`QuotesApi/Domain/`](../../QuotesApi/Domain): `Collection` is the aggregate root and the consistency boundary; `CollectionItem` is an immutable value object mapped as an EF owned type; `ICollectionRepository` is one repository per root rather than per entity; and all mutation goes through the root, which throws on invariant violation instead of letting callers reach inside.

**Day 2 — JWT, OAuth2, OIDC.** Applied in [`AuthController.cs`](../../OrderRefactor/Controllers/AuthController.cs) — self-issued JWTs, 15-minute access tokens, 7-day single-use rotating refresh tokens, which is exactly the shape the card prescribes for an API like this — and in [`Program.cs`](../../OrderRefactor/Program.cs), where a policy scheme routes between my own issuer and an OIDC provider (Entra ID) on the issuer claim.

---
