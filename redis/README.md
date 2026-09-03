# Redis (Day 21 L2)

HybridCache's second tier. Optional — the API runs without it.

```powershell
cd redis
docker compose up -d      # or: .\start.ps1
```

Then point the API at it. Either set the environment variable:

```powershell
$env:Cache__RedisConnectionString = "localhost:6379,abortConnect=false"
```

…or put it in `QuotesApi/appsettings.Development.json` under `Cache`.

`abortConnect=false` matters: without it StackExchange.Redis throws at
startup if Redis is not reachable at that instant, which turns an optional
cache tier into a hard startup dependency — the failure mode Day 19 and Day 20
both deliberately designed around for Service Bus.

Confirm which tier the API is actually using:

```powershell
curl.exe http://localhost:5067/api/cache/stats
```

`"l2": "redis"` means the connection string was picked up; `"l2": "none"`
means it was not, and the cache is running in-process only.

Stop it with `docker compose down` (or `.\stop.ps1`). There is no volume, so
nothing survives the container — which is correct for a cache.
