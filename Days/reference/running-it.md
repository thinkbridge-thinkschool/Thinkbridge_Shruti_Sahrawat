[← Back to full README](../../README.md)

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

**CI status.** [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) runs the three test projects as a matrix on GitHub Actions, each uploading its Cobertura report, and a final `Coverage gate` job merges all three and enforces 80%. The integration leg starts a real SQL Server 2022 container on the runner via Testcontainers. [Latest run](../../actions).

---
