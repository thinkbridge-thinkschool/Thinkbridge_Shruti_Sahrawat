# Status — what is finished, what is scaffolding, what was never built

Written on day 32, the last day. The purpose of this page is that nobody has to
read 31 day-write-ups to find out how far the work actually got, and that
nothing on it is more flattering than the code.

Last verified: 18 September 2026, against `main`.

## Live

| | URL | State |
|---|---|---|
| Web app | `https://black-sea-0f5ad2a00.5.azurestaticapps.net` | 200 |
| API | proxied at `/api/*` from the web app | 401 without a token, as intended |
| Container App | `quotes-api-dev` (`rg-quotes-dev`, UAE North) | revision `--0000005`, image `quotes-api:0.3.0`, Healthy, 100% traffic |
| Database | `quotes-sql-dev-wewdp2fyybrgs.database.windows.net` (`rg-quotes-dev`) | Azure SQL, managed-identity auth |
| Registry | `crquotes33928.azurecr.io` (`rg-quotes-shared`) | |

The Container App's own FQDN answers 401 to everything, and that is correct
rather than broken: linking it as a Static Web App backend enables Easy Auth on
it with the SWA as the identity provider, so the front door is the only way in.
Anyone testing the API directly against
`quotes-api-dev.redfield-acdee432.uaenorth.azurecontainerapps.io` will get 401
on every path including `/health`, and should use the web app's `/api/*` instead.

## QuotesApi — the 31-day application

Finished and deployed: JWT authentication with claims and role checks,
per-endpoint rate limiting, EF Core against Azure SQL with a migration set,
a CQRS read side with both EF and Dapper implementations, HybridCache with
Redis and stampede protection, Polly resilience pipelines with a circuit
breaker, Service Bus publish and consume with a dead-letter path, an outbox,
OpenTelemetry and Application Insights, security headers, and API versioning.

Tested: 56 integration tests against a real SQL Server container, plus the unit
suite, gated at 80% merged line coverage — currently 87.54%.

### Open, on QuotesApi

**Collections ownership is a caller-supplied string.** Fixed today: the
controller had no `[Authorize]` at all, and seven endpoints including a DELETE
were anonymous on the public internet. Still open: `ownerId` arrives from the
query string and the request body, so any *signed-in* caller can read and
modify another owner's collections. A characterisation test,
`Collections_StillLetOneSignedInUserSeeAnothersData_KnownGap`, asserts this is
still true so that fixing it is a failing test rather than a surprise. The fix
touches the read-model contract, the cache keys and the Angular client.

**No authorization fallback policy.** `AddAuthorization()` has no
`FallbackPolicy`, so an endpoint is protected only if somebody remembered.
That is how the above happened. One line at startup would invert the default
and is the single highest-value change left in this repository.

**The container runs as root.** `ContainerUser=root` in `QuotesApi.csproj`
overrides the base image's non-root `app` user. One line to change, untested
under a non-root user, not changed on the last day.

**CVE-2025-6965, High, unpatchable.** `SQLitePCLRaw.lib.e_sqlite3` 2.1.11
carries a SQLite memory-corruption flaw fixed in SQLite 3.50.2, and
SQLitePCLRaw has published no patched release. It arrives through
`Microsoft.EntityFrameworkCore.Sqlite` and affects every project here.
Exploiting it requires attacker-controlled SQL; nothing in this codebase
composes SQL from input, and the deployed configuration uses Azure SQL rather
than SQLite. The native library still ships in the image.

**`SSH.NET` 2024.2.0, High**, in `Quotes.Tests.Integration` via Testcontainers.
Test-only; ships in nothing.

## Capstone — the modular monolith

Finished: the Curation domain with a real aggregate and invariants, EF Core
persistence with value converters and an owned collection, a transactional
outbox in the same DbContext as the aggregate, a non-destructive drain that
acknowledges only what a subscriber accepted, a background relay with a scope
per poll, architecture tests that fail the build if a module references
another module's internals, and 46 tests across four layers.

Coverage, from the `Capstone coverage gate` CI job, gated at 90%:

| Layer | Rate |
|---|---|
| Capstone.Curation.Infrastructure | 99.50% |
| Capstone.Curation.Application | 100.00% |
| Capstone.Sharing.Application | 100.00% |
| Capstone.Sharing.Infrastructure | 100.00% |
| Capstone.SharedKernel | 100.00% |
| Capstone.Curation.Domain | 95.31% |
| Capstone.Api | 94.23% |
| **Capstone.Catalog.Infrastructure** | **28.57%** |
| Merged | 96.16% |

### Not built, from the capstone's own six-day plan

The plan is in [Days/day-28](Days/day-28/README.md). Days 1 and 2 were built
on days 29 and 30. Days 3 to 6 were not:

**Day 3 — the Service Bus relay.** `InProcessRelay` still reads a table in the
same process. Day 31 measured what that costs: `BatchSize` 20 every 250ms caps
delivery at 80 messages a second regardless of load, and a run publishing 170/s
left 7,152 rows unsent after sixty seconds. `RelayHostedService` is the seam.

**Day 4 — persistence for Sharing and Catalog.** Both still keep state in
process memory. This makes the system half-persisted, which is worse than
either extreme: an outbox row survives a restart while the follow that it was
addressed to does not, so the relay correctly delivers a message to zero
followers and marks it sent. The observable result looks exactly like a lost
message.

**Day 5 — the read side and the fan-out ceiling.** Not started. Fan-out on
write is correct for hundreds of followers and wrong for millions.

**Day 6 — deploy it.** The capstone has never been deployed. The blocker is
concrete rather than vague: the only migration set is SQLite's, and a migration
generated against one provider does not work on another — Day 24's Finding 17.
Azure SQL deployment needs a second migration set generated against SQL Server,
which the main solution already demonstrates the pattern for in
`Quotes.Tests.Integration/Migrations/SqlServer`.

### Other known gaps

**Poison messages retry forever.** A row that can never be delivered is retried
four times a second and nothing counts attempts or dead-letters it. Day 19 built
the dead-letter path; the in-process relay does not use it.

**`GET /api/outbox` is unbounded and unauthenticated.** No `Take`, and nothing
prunes the table, so the response grows without limit — measured at 1,952,634
bytes for 10,278 rows. Day 31 predicted this would be a latency problem and
measured that it is a payload problem: 22.5ms empty, 45.0ms at ten thousand
rows.

**The capstone API has no authentication.** `curatorId` comes from the request
body, so the ownership check in `PublishCollectionHandler` compares caller input
against caller input. Demonstrated in [Days/day-31](Days/day-31/README.md) with
a transcript: a stranger publishing to another curator's followers.

## Repository hygiene

Fixed today: the capstone has its own `capstone/Capstone.slnx` so it builds
without the other twelve projects; `QuotesApi.csproj` records the image tag it
actually produces; the deployment docs name resources that exist.

Still true: three hand-maintained lists of projects — the CI matrix, the
solution files, the coverage filters. They stay explicit on purpose, for the
same reason `ModuleBoundaries.Allowed` is a table, and they cost three separate
misses in a month.
