# Day 5 — KQL queries for Application Insights

App Insights resource: quotesapi-insights (Central India).
Telemetry is exported from QuotesApi via Azure.Monitor.OpenTelemetry.AspNetCore
(see Program.cs, commit 4287851).

## Slowest 10 requests in the last hour

requests
| where timestamp > ago(1h)
| top 10 by duration desc
| project timestamp, name, url, duration, resultCode, operation_Id

## Find similar slow endpoints (p95 over 1 second)

Groups by endpoint rather than individual request, so a single warm-up outlier
doesn't dominate. p95 is the useful percentile here — the average hides tail
latency.

requests
| where timestamp > ago(1h)
| summarize
    calls = count(),
    p95 = percentile(duration, 95),
    avg_duration = avg(duration)
  by name
| where p95 > 1000
| order by p95 desc

## Detect N+1 patterns specifically

The N+1 fixed in commit 6c1d36b showed up in Jaeger as many sibling DB spans
under one request. The equivalent signal in App Insights is a high count of SQL
dependencies sharing a single operation_Id.

dependencies
| where timestamp > ago(1h) and type contains "SQL"
| summarize db_calls = count(), db_time = sum(duration) by operation_Id
| where db_calls > 5
| order by db_calls desc

Joining back to the request shows which endpoint is responsible:

dependencies
| where timestamp > ago(1h) and type contains "SQL"
| summarize db_calls = count(), db_time = sum(duration) by operation_Id
| where db_calls > 5
| join kind=inner (
    requests
    | where timestamp > ago(1h)
    | project operation_Id, name, duration
  ) on operation_Id
| project name, db_calls, db_time, total_duration = duration
| order by db_calls desc

## Note on correlation

operation_Id in App Insights is the same TraceId that Serilog logs and Jaeger
displays, so any row above can be pivoted to the full distributed trace.
