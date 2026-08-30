[← Back to full README](../../README.md)

## Day 5 — Diagnosis, Containers, Deployment

**Diagnose a slow endpoint from traces**
[before](../../QuotesApi/docs/day5-before-n1.png) · [after](../../QuotesApi/docs/day5-after-fixed.png) · [`CollectionRepository.cs`](../../QuotesApi/Repositories/CollectionRepository.cs)
A deliberate N+1 in the collections list endpoint: the repository queried each collection separately instead of eager-loading. Jaeger showed it as **seven spans in a sequential staircase at 4.45s**. Replacing the loop with a single `Include` query gave **two spans at 895ms** — 5x faster overall, 14x less database time. The useful detail was that the child DB spans summed to only 1.05s of the 4.45s, so most of the request time was not query execution. The trace also showed the six spans running strictly sequentially with no overlap, so the cost was in doing six separate operations one after another rather than in any of them being slow. Commit `6c1d36b`.

**Container image from `dotnet publish`**
[`QuotesApi/QuotesApi.csproj`](../../QuotesApi/QuotesApi.csproj)
`ContainerRepository`, `ContainerImageTag`, `ContainerBaseImage`, `ContainerUser`. No Dockerfile, no `FROM`, no multi-stage build. Added `/health` via `AddHealthChecks` and `MapHealthChecks` for container liveness; it deliberately checks only that the process is up, so a database blip does not kill the container.

Two deviations from the exercise, both forced. The base image is `aspnet:10.0` rather than `10.0-alpine`: the Alpine build exited 139 with `Error relocating /app/libe_sqlite3.so: fcntl64: symbol not found`, because `SQLitePCLRaw` ships a glibc-linked native library and Alpine uses musl. And `ContainerUser=root`, because the image's default non-root user cannot write SQLite to a mounted volume. Root is the quick fix, not the right one — chowning the volume, or using a database that is not a local file, is. Commit `c2b9834`.

**Azure Container Apps and `azd` deployment**
[`azure.yaml`](../../azure.yaml) · [environment notes](../../QuotesApi/docs/day5-container-apps-env.md) · [health check](../../QuotesApi/docs/day5-azd-health.png)
`azure.yaml` defines QuotesApi as a single `containerapp` service on port 8080. `azd up` provisioned a resource group, Log Analytics workspace, Container Registry, App Insights, portal dashboard, Container Apps environment, and the app itself in **3 minutes 20 seconds**, returning a live HTTPS URL with a valid certificate and no ingress configuration written by hand. Commit `a5894be`.

Two constraints worth recording. Trial subscriptions allow **one Container Apps environment per region**, which surfaced as `MaxNumberOfRegionalEnvironmentsInSubExceeded` mid-deployment, after four other resources had already been created; deployed to South India instead. And SQLite writes to `/tmp` because Container Apps mounts no volume, so the database does not survive a restart.

**A silent telemetry failure, found by looking**
[KQL result](../../QuotesApi/docs/day5-appinsights-kql.png)
The first KQL query against the deployed app returned nothing. `azd` sets `APPLICATIONINSIGHTS_CONNECTION_STRING` on the container, but `Program.cs` read `ApplicationInsights:ConnectionString`, so the conditional `UseAzureMonitor` registration was skipped and the app sent zero telemetry. No startup error, healthy 200s on every endpoint, nothing in the logs — the only symptom was an empty query. Fixed with a fallback to the standard env var and redeployed in 34 seconds. Commit `210bf1f`.

The query grouped four endpoints by p50 and p99. All four sat near 3ms at p50 but spread **37x at p99**, from 6ms to 224ms — they differed almost entirely in the tail. `/health` was the useful control at 0.367ms p50, roughly 9x faster than the rest because it is the only endpoint that touches no database.

**Polly resilience on HTTP calls**
[`QuotesApi/Program.cs`](../../QuotesApi/Program.cs) · [`ResilienceHandlerTests.cs`](../../Quotes.Tests.Unit/ResilienceHandlerTests.cs)
A named HttpClient with `AddResilienceHandler`: 3 retries with jittered exponential backoff, a circuit breaker opening at a 50% failure ratio over 30 seconds with a 15-second break, and a 10-second per-attempt timeout. Every retry and circuit state change is logged; a failure that survives all retries is surfaced as 503, never swallowed. `GET /api/demo/resilience` calls an unreachable port to make the retry logs observable. Two tests use a stub `HttpMessageHandler` rather than a real socket, so they are deterministic and fast: one asserts recovery after two 503s (3 attempts, 200 returned to the caller), one asserts the failure reaches the caller after retries are exhausted (4 attempts, 503 returned). Commit `7cddc45`.

Two things the logs made concrete. The backoff delays were 227ms, 115ms, 678ms rather than a clean 200/400/800 doubling — that is jitter, which exists so that many clients retrying after one outage do not synchronise into a thundering herd. And the whole request took **17.9 seconds despite a 10-second timeout**, because `AddTimeout` is a per-attempt timeout, not a total budget: no single attempt exceeded 10s while the caller waited 18s.

**Smoke test of the deployed API**
[full results](../../QuotesApi/docs/day5-smoke-test.md)
All ten endpoints verified end-to-end against the live URL. Two things it caught that the per-task checks had not — the resilience endpoint returned 404 because the deployed image was one commit behind local, and quote ids reset to 1 because the SQLite file in `/tmp` does not survive a container restart. Testing the whole surface at once found problems that testing each piece separately had missed. The Azure resources have since been deleted, so the URL no longer resolves; the screenshots in [`QuotesApi/docs/`](../../QuotesApi/docs) are the record.

---
