[← Back to full README](../../README.md)

## Day 4 — Observability

**Structured logging with correlation IDs**
[`QuotesApi/Middleware/`](../../QuotesApi/Middleware)
Serilog with a correlation-ID middleware and a console template surfacing `TraceId` on every line. Commit `1aedbb9`.

**OpenTelemetry tracing**
[`QuotesApi/Program.cs`](../../QuotesApi/Program.cs) · [trace screenshot](../../QuotesApi/docs/day4-jaeger-trace.png)
`AddOpenTelemetry` with ASP.NET Core, EF Core, and HttpClient instrumentation, exporting over OTLP gRPC to a local Jaeger container and to the console for debugging. `ConfigureResource` sets `service.name` so traces group correctly in Jaeger. Verified: one request produces a trace at **Depth 2** with the ASP.NET Core server span as parent and two EF Core query spans as children. Serilog and OTel share the same `TraceId` with no extra wiring, because Serilog reads `Activity.Current` — a log line and its span carry the identical hex id. Commit `d8eb19f`.

The session's real lesson was not the OTel configuration. Zero spans appeared for an hour because `Program.cs` had never saved — the file on disk was still the pre-OTel version while I debugged the exporter and the Jaeger connection. Every edit since has been verified with `findstr` before building.

**Application Insights**
[`QuotesApi/Program.cs`](../../QuotesApi/Program.cs) · [KQL queries](../../QuotesApi/docs/day5-kql-queries.md)
`Azure.Monitor.OpenTelemetry.AspNetCore` layered onto the same OpenTelemetry registration, so one set of instrumentation feeds both local Jaeger and Azure; only the exporter differs. The connection string is never hardcoded: it comes from configuration, stored in `dotnet user-secrets` locally, and would be a Key Vault reference in production. Registration is conditional, so the app still runs and exports to Jaeger alone when no connection string is present. Commit `4287851`.

**Configuration with IOptions**
[`OrderRefactor/Configuration/JwtOptions.cs`](../../OrderRefactor/Configuration/JwtOptions.cs)
A typed options class with data annotations, bound with `ValidateDataAnnotations().ValidateOnStart()` and injected as `IOptions<JwtOptions>` into [`AuthController`](../../OrderRefactor/Controllers/AuthController.cs). This replaced scattered `_configuration["Jwt:Key"]` lookups and removed two real defects: a hardcoded fallback signing key that would have silently signed tokens if config went missing, and a token lifetime duplicated as `AddMinutes(15)` in one place and `expires_in = 900` in another. `ValidateOnStart` also turns a too-short signing key from a runtime failure at first login into a startup failure.
`Jwt:Key` is no longer in `appsettings.json`. Local development reads it from `dotnet user-secrets`; production expects a Key Vault reference surfaced as the `Jwt__Key` environment variable; and `Program.cs` refuses to start without it, with a message naming all three. A second dead `Jwt` section — complete with a signing key — was removed from `QuotesApi/appsettings.json`, where nothing had ever read it: that application registers no authentication at all.

The tests supply their own key through the environment rather than through the test host's in-memory configuration, and the reason is a timing detail worth recording. `Program.cs` reads the key while the builder is still being configured, because `AddJwtBearer` needs it to construct `TokenValidationParameters`. `WebApplicationFactory` collects `ConfigureAppConfiguration` delegates in a `DeferredHostBuilder` and replays them when the host is *built* — after the entry point has already read the value. Configuration supplied that way arrives one step too late and is silently ignored. See [`TestJwt`](../../OrderRefactor.Tests/TestJwt.cs). Commit `cff28d4`.

**Continuous integration and the coverage gate**
[`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) · [`coverlet.runsettings`](../../coverlet.runsettings) · [`scripts/check-coverage.py`](../../scripts/check-coverage.py)

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
