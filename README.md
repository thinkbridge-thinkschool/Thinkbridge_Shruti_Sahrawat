# Thinkbridge Backend Assignment — Shruti Sahrawat

Days 1–12. All code in this repository. **207 tests passing** across three test projects.

| Suite | Tests | Runtime |
|---|---|---|
| `OrderRefactor.Tests` | 41 | ~5s |
| `Quotes.Tests.Unit` | 123 | ~1s |
| `Quotes.Tests.Integration` | 43 | ~2m37s, of which ~100s is Docker starting SQL Server 2022 |

Coverage is gated once, on the union of all three suites — see [Day 4](#day-4--observability).

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

**Piece 1 — Dependency injection at depth**
[`QuotesApi/Services/IClock.cs`](QuotesApi/Services/IClock.cs) · [`OrderRefactor/Services/IClock.cs`](OrderRefactor/Services/IClock.cs)
All three lifetimes, chosen rather than defaulted: `DbContext` and the repositories **scoped**, because the change tracker is a per-request unit of work and is not thread-safe; `DiscountCalculator` **transient**, because it is stateless and sharing an instance buys nothing; `IClock` **singleton**, because it holds nothing at all. The classic failure this avoids is the captive dependency — a singleton holding a scoped `DbContext` and quietly corrupting state across requests.

No production code path reads the ambient clock any more. The domain does not depend on `IClock` either: `Quote.Create` and `Collection.AddItem` take a `DateTimeOffset`, and the application layer — the endpoint handler, the MediatR handler, `AuthController`, `OrderService` — owns the clock and passes the instant down. An entity that goes looking for the time cannot be asserted against exactly, and every timestamp test here now asserts an exact value instead of `BeCloseTo(DateTime.UtcNow, 5 seconds)`.

Proven end to end rather than by inspection: [`CreateQuote_WithClockOverridden_PersistsTheClockInstantAndReadsItBack`](Quotes.Tests.Integration/QuoteEndpointsTests.cs) overrides `IClock` in the test host, posts a quote, and reads the row back out of SQL Server to confirm the injected instant was stored — not merely echoed in the response.

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

**Wire Entra ID as the identity provider** — verified end to end. Full account in [`docs/ENTRA-VERIFICATION.md`](OrderRefactor/docs/ENTRA-VERIFICATION.md).
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
A real Microsoft-signed token now authenticates against this API. `GET /api/orders/whoami` returns 401 with no token, 401 with a malformed one, and 200 with an Entra token whose issuer is `https://login.microsoftonline.com/{tenant}/v2.0` and which the policy scheme routes to `EntraJwt`. Microsoft issued it, the handler fetched the signing keys from the published JWKS endpoint and verified the signature asymmetrically, and audience and lifetime both checked out.

**The honest caveat:** that was proven in a personal Entra directory where I am Global Administrator, not in the institutional thinkbridge tenant, which still rejects the `access_as_user` grant with `AADSTS65005` because granting it needs an admin I am not. I routed around the blocker rather than through it. What is proven is that the code validates Entra tokens correctly; what is not proven is that the thinkbridge registration is configured. The difference between them is three values in `appsettings.json` and no code at all.

Two things nearly broke it silently, both producing a 401 that looks like a signing failure. A default registration issues **v1** tokens whose issuer is `sts.windows.net`, which fails against a v2 `Authority` — and would not even reach the Entra validator, since `IssuerSchemeSelector` matches on the `login.microsoftonline.com` prefix and would fall through to the internal scheme. And with `requestedAccessTokenVersion = 2` the `aud` claim is the bare client-id GUID rather than `api://{client-id}`; the value in config was set by decoding the token rather than by assuming.

What *is* proven is the routing decision, which used to be an untestable lambda inside `Program.cs`: reaching it meant booting the app and letting the Entra handler make a live call to `login.microsoftonline.com` for its key set. It now lives in [`IssuerSchemeSelector`](OrderRefactor/Authentication/IssuerSchemeSelector.cs) with [20 tests](OrderRefactor.Tests/IssuerSchemeSelectorTests.cs) covering every branch in microseconds, no network involved.

Extracting it surfaced two defects. The lambda compared the issuer against a hardcoded `"OrderRefactorIssuer"` while the validator it routes to reads `ValidIssuer` from configuration — rename `Jwt:Issuer` and every internally-issued token would have been routed to the Entra validator and rejected. And it matched `"Bearer "` case-sensitively, which RFC 7235 says is wrong. Both are now regression-tested.

**Authorization policies and claims**
`AdminOnly` (claim-based) and `CanEditOwnOrders` (custom assertion) defined in [`Program.cs`](OrderRefactor/Program.cs), applied at [`OrderController.CreateOrder`](OrderRefactor/Controllers/OrderController.cs). Authentication answers *who you are*; policies answer *what you may do*. Roles are claims that change; policies encode rules that don't.

**Lock down the API end-to-end**
[`OrderControllerTests.cs`](OrderRefactor.Tests/OrderControllerTests.cs) + [`RefreshTokenTests.cs`](OrderRefactor.Tests/RefreshTokenTests.cs) — 21 tests: anonymous → 401, authenticated but wrong policy → 403, correct policy → 201, expired token → 401, malformed token → 401, revoked refresh chain → 401.

**The testing pyramid in real terms**
Reflected in the actual shape of the suites: 164 unit tests running in about a second between them, 43 integration tests taking over two minutes. The lesson that stuck is that the pyramid is about *time*, not test count — 43 integration tests consume roughly a hundred times the wall-clock of 164 unit tests, so the ratio that matters is the one on the stopwatch.

Writing `ExceptionHandlingMiddlewareTests` made the point from the other direction. That middleware had no coverage at all, and no integration test could have given it any: nothing in a healthy request throws, so a global exception handler is only reachable by handing it something that fails. A unit test with a hostile `RequestDelegate` does that in microseconds. The code was not hard to test — the tier was wrong.

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
`Jwt:Key` is no longer in `appsettings.json`. Local development reads it from `dotnet user-secrets`; production expects a Key Vault reference surfaced as the `Jwt__Key` environment variable; and `Program.cs` refuses to start without it, with a message naming all three. A second dead `Jwt` section — complete with a signing key — was removed from `QuotesApi/appsettings.json`, where nothing had ever read it: that application registers no authentication at all.

The tests supply their own key through the environment rather than through the test host's in-memory configuration, and the reason is a timing detail worth recording. `Program.cs` reads the key while the builder is still being configured, because `AddJwtBearer` needs it to construct `TokenValidationParameters`. `WebApplicationFactory` collects `ConfigureAppConfiguration` delegates in a `DeferredHostBuilder` and replays them when the host is *built* — after the entry point has already read the value. Configuration supplied that way arrives one step too late and is silently ignored. See [`TestJwt`](OrderRefactor.Tests/TestJwt.cs). Commit `cff28d4`.

**Continuous integration and the coverage gate**
[`.github/workflows/ci.yml`](.github/workflows/ci.yml) · [`coverlet.runsettings`](coverlet.runsettings) · [`scripts/check-coverage.py`](scripts/check-coverage.py)

Three test projects run as a matrix with `fail-fast` off, each uploading its Cobertura report, and a fourth job gates once on the **union** of all three.

Gating each project separately is the thing that does not work, and it is worth being precise about why rather than just lowering the number. `Quotes.Tests.Unit` never boots the application, so 150-odd lines of DI, Serilog, OpenTelemetry and Polly wiring in `Program.cs` sit in its report permanently uncovered — no quantity of honest unit tests moves them. `Quotes.Tests.Integration` boots the app but owns none of the pure domain assertions. Every line either suite covers is a line the codebase has a test for, so the only meaningful question is asked across the whole suite.

Getting to a trustworthy number meant fixing the measurement twice before writing a single test.

*The filters were selecting the wrong code.* The exclude was written `[*.Migrations]*`, which matches an **assembly** named `something.Migrations`. No such assembly exists here — EF Core's generated migrations, snapshots and designer files live inside the `QuotesApi` and `OrderRefactor` assemblies, so roughly 900 lines of generated code were being counted as untested application code. The include swept in `OrderRefactor.Original.OrderController` as well: 255 lines kept deliberately unmodified as the "before" half of the Day 1 refactor, code that exists in order to be bad.

*The merge was double-counting.* Cobertura stores a `<sources>` root and writes each filename relative to it, and coverlet does not choose the same root for every run — one report says `Program.cs` where another says `QuotesApi/Program.cs`. Keying on the raw filename failed to merge them, so files landed in the totals twice: once with real coverage, once at zero. The first merged report read 29.96% and listed `EndpointExtensions.cs` twice at zero covered while the integration suite was demonstrably exercising every endpoint in it.

Neither fix moves a goalpost. Both were the difference between measuring the code the tests are responsible for and measuring something else. The lesson generalises past this repository: **a metric nobody has verified is just a number**, and this one was wrong in two independent ways at once.

Merged line coverage is **84.44%** — 803 of 951 lines across 30 files — against a gate of 80%. It was 40% under the broken filters, and 29.96% under the broken merge.

What is still uncovered, and why each one is left rather than papered over:

```
missing  covered  total  file
     82       38    120  OrderRefactor/Controllers/AuthController.cs
     21        4     25  OrderRefactor/Repositories/OrderRepository.cs
     14       38     52  QuotesApi/Controllers/CollectionsController.cs
     14       14     28  QuotesApi/Repositories/CollectionRepository.cs
      6       79     85  OrderRefactor/Program.cs
      4        4      8  OrderRefactor/Controllers/OrderController.cs
      3      124    127  QuotesApi/Program.cs
      3       25     28  OrderRefactor/Authentication/IssuerSchemeSelector.cs
      1       13     14  OrderRefactor/Data/OrdersDbContext.cs
```

**The surprise is the first row, and I do not have a satisfying explanation for it yet.** `AuthController` is the most heavily tested file in the repository by intent — `RefreshTokenTests` drives login, rotation, replay detection, family revocation and logout end to end through a real HTTP pipeline — and it comes out the *least* covered at 32%. The branches I know are untested are small: invalid credentials, an empty refresh token, an expired-but-not-revoked token. Those are perhaps fifteen lines, not eighty-two. Either coverlet is attributing lines to this file that the tests genuinely never execute, or there is more dead code in it than I think. Reading the per-line report to find out is the next thing I would do, and I would rather record the discrepancy than round it off.

The other rows are ordinary. `OrderRepository` at 4/25 is exercised only through `OrderService`, which the unit tests substitute away — its real EF calls run in no test. `CollectionsController` and `CollectionRepository` keep the error branches that no test provokes. Both `Program.cs` files are near-total because the integration suites boot them; the handful of missing lines are startup paths that only run when configuration is absent.

None of this is padding. Covering `OrderRepository` properly means an EF-backed test that writes and reads real rows, which is worth doing and is not the same as adding assertions until a percentage moves.

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

## Bugs these tests caught

**A startup bug that would have broken any clean deployment.** All 23 integration tests failed on their first run — inside `Program.cs`, not in test code. `Quote.IsDeleted` existed on the model but had never been captured in a migration, so `Database.Migrate()` threw `PendingModelChangesWarning` against any fresh database. My local `quotes.db` predated the drift, so it had never surfaced in development. A clean clone would not have booted. Fixed in [`20260812113000_AddQuoteIsDeleted.cs`](QuotesApi/Migrations/20260812113000_AddQuoteIsDeleted.cs).

**A regression I introduced myself, caught within the hour.** Adding `[Authorize(Policy = "AdminOnly")]` to `CreateOrder` immediately broke an existing Day 2 test that posted without a token — it started returning 401 before reaching the logic under test. The suite caught it the same hour I wrote it. I fixed the test, not the policy.

**Every error response advertised the wrong media type.** `ExceptionHandlingMiddleware` set `Response.ContentType = "application/problem+json"` and then called `WriteAsJsonAsync`, which assigns `"application/json; charset=utf-8"` unconditionally and silently overwrote it. A client keying off `application/problem+json` would not have recognised a single one of these as a problem document. Invisible until a test asserted the header rather than the body.

**A hardcoded exporter endpoint that only worked on one machine.** `Program.cs` shipped spans to a literal `http://localhost:4317`. In Azure that meant every span was exported into nothing — no error, no log line, the same silent shape as the App Insights connection-string bug on Day 5, sitting one layer beneath it. Under `dotnet test` the same literal was loud instead of silent: with no collector listening, every export waited out its timeout and every `WebApplicationFactory` disposal blocked on a final flush. The integration suite went from seconds to **41 minutes**. The endpoint now comes from configuration, and no configured endpoint means no exporter is registered.

**The auth scheme router disagreed with the validator it routes to.** The policy-scheme lambda compared the token issuer against a hardcoded `"OrderRefactorIssuer"` while the validator read `ValidIssuer` from configuration. Renaming `Jwt:Issuer` would have routed every internally-issued token to the Entra validator, which would have rejected all of them. It also matched `"Bearer "` case-sensitively, against RFC 7235. Neither was reachable by test until the logic was pulled out of `Program.cs`.

**The suite was passing because of a file on my laptop.** After `Jwt:Key` left `appsettings.json`, the tests kept going green — on my machine, via `dotnet user-secrets`. The moment the test host stopped running as `Development` for an unrelated reason, eleven tests failed at once. A fresh clone on a CI runner would have failed the same way the first time anyone else ran it. The key now arrives through the environment, the way production delivers it.

**A test that asserted the old, broken behaviour.** `CreateQuote_EvenWithFakeClockOverridden_CreatedAtStillReflectsRealSystemTime` documented the `IClock` gap honestly, and was correct when written. Fixing the endpoint made it wrong, and it had been failing since — a red test that read like a known limitation. It is now two positive assertions, one of which reads the row back out of SQL Server to prove the injected instant was persisted rather than merely echoed.

---

## Running it

```bash
dotnet user-secrets set "Jwt:Key" "<32+ characters>" --project OrderRefactor   # once

dotnet test OrderRefactor.Tests        # 41
dotnet test Quotes.Tests.Unit          # 123
dotnet test Quotes.Tests.Integration   # 43 — requires Docker

./scripts/coverage.ps1                 # all three, merged, gated at 80%
```

The user secret is needed only to `dotnet run` the API. The tests supply their own signing key through the environment, so a fresh clone tests green without any local setup.

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

**CI status.** [`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs the three test projects as a matrix on GitHub Actions, each uploading its Cobertura report, and a final `Coverage gate` job merges all three and enforces 80%. The integration leg starts a real SQL Server 2022 container on the runner via Testcontainers. [Latest run](../../actions).

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

Window functions and set operations are in the same folder: [`sql/WINDOW-FUNCTIONS.md`](sql/WINDOW-FUNCTIONS.md) covers `ROW_NUMBER`, `RANK`, `LAG` and a running total; [`sql/SET-OPERATIONS.md`](sql/SET-OPERATIONS.md) translates three business questions into `EXCEPT`, `INTERSECT` and `UNION`, with a note on why each operator was the right one.

---

## Day 8 — Execution plans and indexes

[`sql/INDEXES.md`](sql/INDEXES.md) — a clustered and two non-clustered indexes over 100k generated rows, with `SET STATISTICS IO ON` logical-read counts before and after each one, and the write-side cost measured separately rather than asserted.

[`sql/COVERING-INDEXES.md`](sql/COVERING-INDEXES.md) — a query doing a key lookup, an index with `INCLUDE`d columns that eliminates it, and both plans side by side.

---

## Day 9 — Transactions and isolation

[`sql/DEADLOCK.md`](sql/DEADLOCK.md) — a two-resource deadlock reproduced across two sessions with opposite lock ordering, the deadlock graph, and the fix by consistent ordering.

[`sql/ISOLATION-LEVELS.md`](sql/ISOLATION-LEVELS.md) — dirty read, non-repeatable read and phantom read each reproduced and then prevented, one isolation level at a time. Not on the assigned list; kept because the deadlock only makes sense next to it.

---

## Day 10 — EF Core internals

[`Quotes.Benchmark/README.md`](Quotes.Benchmark/README.md) — change tracker versus `AsNoTracking` over 10k rows, measured with BenchmarkDotNet rather than a stopwatch, so the allocation numbers mean something. Includes the case where `AsNoTracking` is the wrong choice.

[`Quotes.Benchmark/PROJECTIONS.md`](Quotes.Benchmark/PROJECTIONS.md) — the SQL EF generates for a whole-entity query, the leaner SQL after projecting to a DTO, and one accidental client-side evaluation caught and fixed.

---

## Day 11 — Performance

[`perf/README.md`](perf/README.md) — a deliberately slow endpoint profiled under k6 load: p50/p99, the N+1 SQL it emits, and the execution plan.

[`perf/FIX.md`](perf/FIX.md) — the same endpoint after eliminating the N+1 and indexing `Author`. **p99 down 241x** against a 10x target, with before and after plans. Two overclaims in the original write-up were corrected afterwards; the corrections are in the commit history rather than quietly edited out.

---

## Day 12 — CQRS and Dapper

[`QuotesApi/Features/Collections/README.md`](QuotesApi/Features/Collections/README.md) — the Collections feature split into a write path through MediatR commands and the aggregate, and a read path projecting straight from the `DbContext` with `PreviewSize` pushed into SQL via a per-collection `ROW_NUMBER`.

[`QuotesApi/Features/Collections/DAPPER.md`](QuotesApi/Features/Collections/DAPPER.md) — the same read path in hand-written SQL, timed against the EF version, with the rule I would give a teammate for when to drop to Dapper.

Both implementations are held to the same contract by [`CollectionSummariesReadPathTests`](Quotes.Tests.Integration/CollectionSummariesReadPathTests.cs), which asserts they return identical results at four preview sizes, with an owner filter, for an empty collection, and when a previewed quote has been deleted underneath them. A faster query that answers a different question is not an optimisation.
