# Day 5 — Smoke test of the deployed API

Target: https://quotes-api.politesea-83b94ff4.southindia.azurecontainerapps.io
Run: 14 Aug 2026, against commit 7cddc45 deployed via azd

| Endpoint                          | Expected | Actual |
|-----------------------------------|----------|--------|
| GET /health                       | 200      | 200    |
| GET /api/quotes?page=1&size=5     | 200      | 200    |
| POST /api/quotes                  | 201      | 201    |
| GET /api/quotes/1                 | 200      | 200    |
| GET /api/quotes/9999              | 404      | 404    |
| GET /api/collections              | 200      | 200    |
| POST /api/collections             | 201      | 201    |
| GET /api/quotes (no params)       | 400      | 400    |
| POST /api/quotes (empty fields)   | 400      | 400    |
| GET /api/demo/resilience          | 503      | 503    |

All endpoints behave as expected end-to-end.

## What feels fragile

1. The database does not survive a restart. Both POSTs returned id 1, even
   though earlier testing had reached id 7 — the SQLite file lives in /tmp
   because Container Apps mounts no volume, so every restart or redeploy starts
   from an empty database. A real deployment needs Azure SQL, Postgres, or a
   mounted Azure Files share.

2. Error handling differs between environments. GET /api/quotes with no
   parameters returns 400 in Azure but 500 locally: in Production, ASP.NET Core
   handles the missing-required-parameter case before it reaches the custom
   exception middleware. Local testing does not fully predict deployed behaviour.

3. Drift between local and deployed. The first smoke run returned 404 for
   /api/demo/resilience because the deployed image was one commit behind local.
   Nothing warns you that the running image is stale.

4. page and size are required with no defaults, so a bare GET on a collection
   endpoint fails. That is an API design wart rather than a bug, but it will
   surprise any client.
