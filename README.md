# Thinkbridge Backend Assignment — Shruti Sahrawat

Days 1–7. All code in this repository. **141 tests passing** across three test projects. CI is currently red on a coverage gate, not on test failures — see [CI status](#running-it).

| Suite | Tests | Runtime |
|---|---|---|
| `OrderRefactor.Tests` | 21 | ~2s |
| `Quotes.Tests.Unit` | 97 | ~1s |
| `Quotes.Tests.Integration` | 23 | ~14s (real SQL Server 2022 container) |

Two applications: **QuotesApi** (minimal API, DDD aggregate) and **OrderRefactor** (layered refactor, JWT auth, Entra ID).

---

## Day 1 — Foundations

**Piece 1 — Hello in two languages**
[`hello-cs/Program.cs`](hello-cs/Program.cs) · [`hello-ts/hello.ts`](hello-ts/hello.ts)
C# needs a `.csproj` and an SDK before it will run anything; Node 24 executes TypeScript directly with no build step and no config file. The contrast is the point — one runtime asks you to declare structure up front, the other asks for nothing.

**Piece 2 — Minimal ASP.NET Core API**
[`QuotesApi/Extensions/EndpointExtensions.cs`](QuotesApi/Extensions/EndpointExtensions.cs) · [`QuotesApi/Repositories/QuoteRepository.cs`](QuotesApi/Repositories/QuoteRepository.cs)
Four endpoints on `/api/quotes` — paged list, create, get by id, delete. EF Core + SQLite with migrations applied at startup, scoped `IQuoteRepository` via DI, `ValidationProblemDetails` on invalid input, `CancellationToken` flowing into every EF query, structured logging via `ILogger<T>`, and `ProblemDetails` from [exception middleware](QuotesApi/Middleware/ExceptionHandlingMiddleware.cs). `Program.cs` stays under 120 lines by splitting into `AddInfrastructure()` and `MapQuoteEndpoints()` extension methods.

**Piece 3 — Refactor a god-method controller**
Before: [`OrderRefactor/Original/OrderController.cs`](OrderRefactor/Original/OrderController.cs) — the ~250-line original, saved unmodified.
Prompt that generated it: [`OrderRefactor/Original/PROMPT.md`](OrderRefactor/Original/PROMPT.md)
Analysis: [`OrderRefactor/REFACTOR_NOTES.md`](OrderRefactor/REFACTOR_NOTES.md) — 10+ distinct smells, each with its consequence and intended fix, written before touching a line of code.
After: [`Controllers/OrderController.cs`](OrderRefactor/Controllers/OrderController.cs) → [`Services/OrderService.cs`](OrderRefactor/Services/OrderService.cs) → [`Repositories/`](OrderRefactor/Repositories) — split into layers wired by DI, async end-to-end with cancellation, typed return shapes, and empty catches replaced with narrow handlers that log and rethrow.

**Piece 4 — Real AI-assisted work**
[`AI_REFLECTION.md`](AI_REFLECTION.md) — where Claude Code helped, where it over-engineered and I pushed back, and where Copilot suggested something subtly wrong.

**Piece 5 — Build a real aggregate**
[`QuotesApi/Domain/Collection.cs`](QuotesApi/Domain/Collection.cs) · [`QuotesApi/Domain/CollectionItem.cs`](QuotesApi/Domain/CollectionItem.cs)
An aggregate root that enforces its own invariants: name 3–80 characters, maximum 50 items, no duplicate quote IDs, positive quote ID required. `CollectionItem` is an immutable value object mapped as an EF owned type. Every mutation goes through `AddItem`/`RemoveItem`, which throw rather than letting callers touch the collection directly — so the aggregate is consistent after every operation, not merely by convention. Endpoints: [`CollectionsController.cs`](QuotesApi/Controllers/CollectionsController.cs).

---

## Day 2 — Architecture and Authentication

**Piece 1 — Dependency injection at depth** *(partial — see note)*
[`QuotesApi/Services/IClock.cs`](QuotesApi/Services/IClock.cs) · [`SystemClock.cs`](QuotesApi/Services/SystemClock.cs), registered as a singleton in [`Program.cs`](QuotesApi/Program.cs). Repositories and `DbContext` scoped; `DiscountCalculator` transient.
**Known gap:** `IClock` is registered and covered by fake-clock tests against the `Quote.Create(author, text, clock)` overload, but `EndpointExtensions` still calls the two-argument overload, so the clock is never consulted on the live request path. `CollectionItem` and `AuthController` also still call `DateTime.UtcNow` directly. The abstraction exists and is testable; it is not yet threaded through production.

**Piece 2 — async/await with cancellation through layers**
`CancellationToken` is the last parameter on every I/O method and flows controller → service → repository → EF. See [`IOrderRepository.cs`](OrderRefactor/Repositories/IOrderRepository.cs) and [`QuoteRepository.cs`](QuotesApi/Repositories/QuoteRepository.cs). Cancellation is tested, not assumed: [`CollectionsControllerCancellationTests.cs`](OrderRefactor.Tests/CollectionsControllerCancellationTests.cs).

**Piece 3 — Test the domain layer**
[`OrderRefactor.Tests/CollectionDomainTests.cs`](OrderRefactor.Tests/CollectionDomainTests.cs), extended considerably in [`Quotes.Tests.Unit/CollectionTests.cs`](Quotes.Tests.Unit/CollectionTests.cs) — every `Collection` invariant including the 49th/50th/51st item boundary. Pure and fast: no DbContext, no fixtures, no setup methods.

**Piece 4 — AI-assisted refactor: anemic to rich**
[`QuotesApi/Models/Quote.cs`](QuotesApi/Models/Quote.cs) — private setters, private constructor, a static `Quote.Create` factory validating author (1–200 chars) and text (1–1000 chars) with trimming, and `SoftDelete()` instead of a publicly mutable flag. Rationale and the bug the anemic version would have shipped: [`WHY.md`](WHY.md). Tests: [`Quotes.Tests.Unit/QuoteTests.cs`](Quotes.Tests.Unit/QuoteTests.cs).

**Piece 5 — JWT auth with my own issuer**
[`OrderRefactor/Controllers/AuthController.cs`](OrderRefactor/Controllers/AuthController.cs) — `POST /api/auth/login` returns `access_token`, `refresh_token`, and `expires_in`. HS256, signed with a 256-bit key read from configuration, never hardcoded.

**Piece 6 — Refresh tokens with rotation and reuse detection**
Same file. Refresh tokens are stored hashed, never in plaintext ([`Models/RefreshToken.cs`](OrderRefactor/Models/RefreshToken.cs): `TokenHash`, `UserId`, `ExpiresAt`, `RevokedAt`, `ReplacedByToken`). Every refresh rotates the pair and marks the old token replaced. **Presenting an already-rotated token revokes the entire family for that user** and forces re-authentication — so a leaked token cannot be used twice, and the theft is detected rather than silently exploited.
Proven end-to-end in [`RefreshTokenTests.cs`](OrderRefactor.Tests/RefreshTokenTests.cs): log in, refresh once, replay the spent token → 401, then confirm the legitimate user's current token is dead too.

---

## Day 3 — Enterprise Auth and Testing

**Wire Entra ID as the identity provider** *(config complete, live token untested — see note)*
[`OrderRefactor/Program.cs`](OrderRefactor/Program.cs) registers two bearer schemes behind a policy scheme:
Request with Bearer token
↓
PolicyScheme reads the issuer claim (reads only — no validation yet)
↓
iss == "OrderRefactorIssuer"?
├─ yes → InternalJwt symmetric key, my own tokens
└─ no → EntraJwt Microsoft's public keys, fetched from Authority
↓
[Authorize] resolves as normal — controllers unchanged
Entra configuration (`TenantId`, `ClientId`, `Audience`) lives in `appsettings.json`; these are public identifiers, not secrets. Authority is `https://login.microsoftonline.com/{tenant}/v2.0`. No client secret is needed anywhere — an API that only validates tokens uses Microsoft's published signing keys.
**Known gap:** the application is registered in Entra and the code path is in place, but I could not obtain a real Entra access token to verify end-to-end. The institutional tenant rejected the `access_as_user` scope grant (`AADSTS65005`). The internal JWT path is verified working; the Entra branch is unverified against a live token.

**Authorization policies and claims**
`AdminOnly` (claim-based) and `CanEditOwnOrders` (custom assertion) defined in [`Program.cs`](OrderRefactor/Program.cs), applied at [`OrderController.CreateOrder`](OrderRefactor/Controllers/OrderController.cs). Authentication answers *who you are*; policies answer *what you may do*. Roles are claims that change; policies encode rules that don't.

**Lock down the API end-to-end**
[`OrderControllerTests.cs`](OrderRefactor.Tests/OrderControllerTests.cs) + [`RefreshTokenTests.cs`](OrderRefactor.Tests/RefreshTokenTests.cs) — 21 tests: anonymous → 401, authenticated but wrong policy → 403, correct policy → 201, expired token → 401, malformed token → 401, revoked refresh chain → 401.

**The testing pyramid in real terms**
Reflected in the actual shape of the suites: 97 unit tests at ~3ms each, 44 integration tests at ~200ms–600ms, no end-to-end layer. The lesson that stuck is that the pyramid is about *time*, not test count — 23 integration tests consume more wall-clock than 97 unit tests, so the ratio that matters is the one on the stopwatch.

**xUnit with FluentAssertions**
[`Quotes.Tests.Unit/`](Quotes.Tests.Unit) — one test class per production class, `Method_StateUnderTest_ExpectedBehavior` naming, explicit AAA in every test, no `SetUp` hiding arrangement, `[Theory]`/`[InlineData]` for boundaries. NSubstitute for `IOrderRepository`, `IConfiguration`, and `ILogger<T>`.

**Integration tests with WebApplicationFactory**
[`Quotes.Tests.Integration/`](Quotes.Tests.Integration) — 23 tests booting the real application: real middleware pipeline, real DI graph, real EF. A fresh database and `HttpClient` per test, no shared state between tests. `ProblemDetails` and `ValidationProblemDetails` response shapes are asserted, not just status codes.

**Real SQL Server in CI with Testcontainers**
[`MsSqlContainerFixture.cs`](Quotes.Tests.Integration/MsSqlContainerFixture.cs) — one SQL Server 2022 container per assembly run via `IAsyncLifetime` + `ICollectionFixture`, with each test getting its own database on that shared container. The suite goes 2s → 14s: the honest cost of testing against a real engine.
The SQLite migrations could not be replayed against SQL Server. They bake in literal `TEXT` column types and a `Sqlite:Autoincrement` annotation that SQL Server silently ignores, producing a table with no `IDENTITY` — so every insert fails. The fix is not translation but a separate SQL-Server-native migration set inside the test project ([`Migrations/SqlServer/`](Quotes.Tests.Integration/Migrations/SqlServer)), wired via `MigrationsAssembly`. Zero production changes; the SQLite app still runs unmodified.

---

## Day 4 — Observability

**Structured logging with correlation IDs**
[`QuotesApi/Middleware/`](QuotesApi/Middleware)
Serilog with a correlation-ID middleware and a console template surfacing `TraceId` on every line. Commit `1aedbb9`.

**OpenTelemetry tracing**
[`QuotesApi/Program.cs`](QuotesApi/Program.cs) · [trace screenshot](QuotesApi/docs/day4-jaeger-trace.png)
`AddOpenTelemetry` with ASP.NET Core, EF Core, and HttpClient instrumentation, exporting over OTLP gRPC to a local Jaeger container and to the console for debugging. `ConfigureResource` sets `service.name` so traces group correctly in Jaeger. Verified: one request produces a trace at **Depth 2** with the ASP.NET Core server span as parent and two EF Core query spans as children. Serilog and OTel share the same `TraceId` with no extra wiring, because Serilog reads `Activity.Current` — a log line and its span carry the identical hex id. Commit `d8eb19f`.

The session's real lesson was not the OTel configuration. Zero spans appeared for an hour because `Program.cs` had never saved — the file on disk was still the pre-OTel version while I debugged the exporter and the Jaeger connection. Every edit since has been verified with `findstr` before building.

**Application Insights**
[`QuotesApi/Program.cs`](QuotesApi/Program.cs) · [KQL queries](QuotesApi/docs/day5-kql-queries.md)
`Azure.Monitor.OpenTelemetry.AspNetCore` layered onto the same OpenTelemetry registration, so one set of instrumentation feeds both local Jaeger and Azure; only the exporter differs. The connection string is never hardcoded: it comes from configuration, stored in `dotnet user-secrets` locally, and would be a Key Vault reference in production. Registration is conditional, so the app still runs and exports to Jaeger alone when no connection string is present. Commit `4287851`.

**Configuration with IOptions**
[`OrderRefactor/Configuration/JwtOptions.cs`](OrderRefactor/Configuration/JwtOptions.cs)
A typed options class with data annotations, bound with `ValidateDataAnnotations().ValidateOnStart()` and injected as `IOptions<JwtOptions>` into [`AuthController`](OrderRefactor/Controllers/AuthController.cs). This replaced scattered `_configuration["Jwt:Key"]` lookups and removed two real defects: a hardcoded fallback signing key that would have silently signed tokens if config went missing, and a token lifetime duplicated as `AddMinutes(15)` in one place and `expires_in = 900` in another. `ValidateOnStart` also turns a too-short signing key from a runtime failure at first login into a startup failure.
**Known gap:** `Jwt:Key` is still committed in `appsettings.json`. It belongs in user-secrets locally and Key Vault in production; left in place to avoid changing test fixtures. Commit `cff28d4`.

---

## Day 5 — Diagnosis, Containers, Deployment

**Diagnose a slow endpoint from traces**
[before](QuotesApi/docs/day5-before-n1.png) · [after](QuotesApi/docs/day5-after-fixed.png) · [`CollectionRepository.cs`](QuotesApi/Repositories/CollectionRepository.cs)
A deliberate N+1 in the collections list endpoint: the repository queried each collection separately instead of eager-loading. Jaeger showed it as **seven spans in a sequential staircase at 4.45s**. Replacing the loop with a single `Include` query gave **two spans at 895ms** — 5x faster overall, 14x less database time. The useful detail was that the child DB spans summed to only 1.05s of the 4.45s, so most of the request time was not query execution. The trace also showed the six spans running strictly sequentially with no overlap, so the cost was in doing six separate operations one after another rather than in any of them being slow. Commit `6c1d36b`.

**Container image from `dotnet publish`**
[`QuotesApi/QuotesApi.csproj`](QuotesApi/QuotesApi.csproj)
`ContainerRepository`, `ContainerImageTag`, `ContainerBaseImage`, `ContainerUser`. No Dockerfile, no `FROM`, no multi-stage build. Added `/health` via `AddHealthChecks` and `MapHealthChecks` for container liveness; it deliberately checks only that the process is up, so a database blip does not kill the container.

Two deviations from the exercise, both forced. The base image is `aspnet:10.0` rather than `10.0-alpine`: the Alpine build exited 139 with `Error relocating /app/libe_sqlite3.so: fcntl64: symbol not found`, because `SQLitePCLRaw` ships a glibc-linked native library and Alpine uses musl. And `ContainerUser=root`, because the image's default non-root user cannot write SQLite to a mounted volume. Root is the quick fix, not the right one — chowning the volume, or using a database that is not a local file, is. Commit `c2b9834`.

**Azure Container Apps and `azd` deployment**
[`azure.yaml`](azure.yaml) · [environment notes](QuotesApi/docs/day5-container-apps-env.md) · [health check](QuotesApi/docs/day5-azd-health.png)
`azure.yaml` defines QuotesApi as a single `containerapp` service on port 8080. `azd up` provisioned a resource group, Log Analytics workspace, Container Registry, App Insights, portal dashboard, Container Apps environment, and the app itself in **3 minutes 20 seconds**, returning a live HTTPS URL with a valid certificate and no ingress configuration written by hand. Commit `a5894be`.

Two constraints worth recording. Trial subscriptions allow **one Container Apps environment per region**, which surfaced as `MaxNumberOfRegionalEnvironmentsInSubExceeded` mid-deployment, after four other resources had already been created; deployed to South India instead. And SQLite writes to `/tmp` because Container Apps mounts no volume, so the database does not survive a restart.

**A silent telemetry failure, found by looking**
[KQL result](QuotesApi/docs/day5-appinsights-kql.png)
The first KQL query against the deployed app returned nothing. `azd` sets `APPLICATIONINSIGHTS_CONNECTION_STRING` on the container, but `Program.cs` read `ApplicationInsights:ConnectionString`, so the conditional `UseAzureMonitor` registration was skipped and the app sent zero telemetry. No startup error, healthy 200s on every endpoint, nothing in the logs — the only symptom was an empty query. Fixed with a fallback to the standard env var and redeployed in 34 seconds. Commit `210bf1f`.

The query grouped four endpoints by p50 and p99. All four sat near 3ms at p50 but spread **37x at p99**, from 6ms to 224ms — they differed almost entirely in the tail. `/health` was the useful control at 0.367ms p50, roughly 9x faster than the rest because it is the only endpoint that touches no database.

**Polly resilience on HTTP calls**
[`QuotesApi/Program.cs`](QuotesApi/Program.cs) · [`ResilienceHandlerTests.cs`](Quotes.Tests.Unit/ResilienceHandlerTests.cs)
A named HttpClient with `AddResilienceHandler`: 3 retries with jittered exponential backoff, a circuit breaker opening at a 50% failure ratio over 30 seconds with a 15-second break, and a 10-second per-attempt timeout. Every retry and circuit state change is logged; a failure that survives all retries is surfaced as 503, never swallowed. `GET /api/demo/resilience` calls an unreachable port to make the retry logs observable. Two tests use a stub `HttpMessageHandler` rather than a real socket, so they are deterministic and fast: one asserts recovery after two 503s (3 attempts, 200 returned to the caller), one asserts the failure reaches the caller after retries are exhausted (4 attempts, 503 returned). Commit `7cddc45`.

Two things the logs made concrete. The backoff delays were 227ms, 115ms, 678ms rather than a clean 200/400/800 doubling — that is jitter, which exists so that many clients retrying after one outage do not synchronise into a thundering herd. And the whole request took **17.9 seconds despite a 10-second timeout**, because `AddTimeout` is a per-attempt timeout, not a total budget: no single attempt exceeded 10s while the caller waited 18s.

**Smoke test of the deployed API**
[full results](QuotesApi/docs/day5-smoke-test.md)
All ten endpoints verified end-to-end against the live URL. Two things it caught that the per-task checks had not — the resilience endpoint returned 404 because the deployed image was one commit behind local, and quote ids reset to 1 because the SQLite file in `/tmp` does not survive a container restart. Testing the whole surface at once found problems that testing each piece separately had missed. The Azure resources have since been deleted, so the URL no longer resolves; the screenshots in [`QuotesApi/docs/`](QuotesApi/docs) are the record.

---

## Concept cards

Three cards were conceptual rather than build tasks. Where each one landed in the code:

**Day 1 — Tools check.** .NET SDK 10.0.302, Node 24 (runs `hello.ts` natively, no `tsc` step), Git, VS Code with C# Dev Kit, Copilot, Claude Code — the last used for the Day 1 refactor, the Day 2 rich-model rewrite, and three Day 3 test projects.

**Day 2 — Entity, value object, aggregate root.** Demonstrated in [`QuotesApi/Domain/`](QuotesApi/Domain): `Collection` is the aggregate root and the consistency boundary; `CollectionItem` is an immutable value object mapped as an EF owned type; `ICollectionRepository` is one repository per root rather than per entity; and all mutation goes through the root, which throws on invariant violation instead of letting callers reach inside.

**Day 2 — JWT, OAuth2, OIDC.** Applied in [`AuthController.cs`](OrderRefactor/Controllers/AuthController.cs) — self-issued JWTs, 15-minute access tokens, 7-day single-use rotating refresh tokens, which is exactly the shape the card prescribes for an API like this — and in [`Program.cs`](OrderRefactor/Program.cs), where a policy scheme routes between my own issuer and an OIDC provider (Entra ID) on the issuer claim.

---

## Two bugs these tests caught

**A startup bug that would have broken any clean deployment.** All 23 integration tests failed on their first run — inside `Program.cs`, not in test code. `Quote.IsDeleted` existed on the model but had never been captured in a migration, so `Database.Migrate()` threw `PendingModelChangesWarning` against any fresh database. My local `quotes.db` predated the drift, so it had never surfaced in development. A clean clone would not have booted. Fixed in [`20260812113000_AddQuoteIsDeleted.cs`](QuotesApi/Migrations/20260812113000_AddQuoteIsDeleted.cs).

**A regression I introduced myself, caught within the hour.** Adding `[Authorize(Policy = "AdminOnly")]` to `CreateOrder` immediately broke an existing Day 2 test that posted without a token — it started returning 401 before reaching the logic under test. The suite caught it the same hour I wrote it. I fixed the test, not the policy.

---

## Running it

```bash
dotnet test OrderRefactor.Tests        # 21
dotnet test Quotes.Tests.Unit          # 97
dotnet test Quotes.Tests.Integration   # 23 — requires Docker
```

Local observability stack:

```bash
docker run -d --name jaeger -p 16686:16686 -p 4317:4317 -p 4318:4318 jaegertracing/all-in-one:latest
dotnet run --project QuotesApi
# traces appear at http://localhost:16686
```

Container image, no Dockerfile:

```bash
dotnet publish QuotesApi --os linux --arch x64 /t:PublishContainer
docker run -d -p 8080:8080 -v quotes-data:/data \
  -e "ConnectionStrings__Default=Data Source=/data/quotes.db" quotes-api:0.1.0
curl http://localhost:8080/health   # Healthy
```

**CI status.** [`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs all three projects as separate jobs on GitHub Actions. All three `dotnet test` steps pass, including the integration job, which starts a real SQL Server 2022 container on the runner via Testcontainers. All three jobs then fail on a final *Enforce line coverage threshold* step: the gate is set to 70% and actual line coverage is 40%. The gate was introduced in `5cd551d` and this codebase has never met it. Raising coverage honestly means testing `CollectionRepository`, the endpoint handlers, and `ExceptionHandlingMiddleware` — not padding the number against `Original/OrderController.cs`, which exists in order to be bad. I would rather agree a threshold that reflects reality and ratchet it upward than write tests that move a percentage without adding confidence. [Latest run](../../actions).

---

## Day 7 — Joins and CTEs at depth

**Author quote counts with most-recent quote, in one statement**
[`sql/author-summary.sql`](sql/author-summary.sql) · [full notes and query plans](sql/README.md)

```sql
WITH ranked AS (
    SELECT Author, Text, CreatedAt,
           ROW_NUMBER() OVER (PARTITION BY Author ORDER BY CreatedAt DESC) AS rn,
           COUNT(*)     OVER (PARTITION BY Author)                         AS quote_count
    FROM Quotes
    WHERE IsDeleted = 0
)
SELECT Author, quote_count, Text AS most_recent_quote, CreatedAt AS most_recent_at
FROM ranked
WHERE rn = 1
ORDER BY quote_count DESC, Author
LIMIT 10;
```

The two required values pull in opposite directions: the count is an aggregate that collapses rows, while the most-recent quote is a column from one specific row that needs those rows kept. Window functions compute over a partition without collapsing it, so `ROW_NUMBER` picks the newest row per author and `COUNT(*) OVER` produces the count in the same pass.

**Why a CTE rather than a correlated subquery.** `EXPLAIN QUERY PLAN` shows the correlated version carrying a `CORRELATED SCALAR SUBQUERY` node with its own `SCAN q2` — the inner query depends on the outer row, so it re-scans and re-sorts once per author group. The CTE version has no `CORRELATED` node; it is nested co-routines pipelining from a single `SCAN Quotes`. Trade-off worth naming: the CTE version uses three `USE TEMP B-TREE` sorts against the correlated version's two, so it front-loads more sorting. On 23 rows neither is measurably slow — the CTE wins on how it scales, not on this dataset.

Same shape as the Day 5 N+1: one query per collection turned a request into six sequential round trips. A correlated subquery is that pattern moved inside the database engine.
