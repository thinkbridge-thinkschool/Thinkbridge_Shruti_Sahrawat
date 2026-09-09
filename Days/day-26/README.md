# Day 26 — App Insights + KQL

The exercise: wire OpenTelemetry into Application Insights, write KQL for
p50/p99 by endpoint, the dependency breakdown and an error-rate alert, and
confirm distributed tracing stitches API → worker → DB.

The last of those turned out not to be a confirmation. It was a bug report.

## The finding the day is actually about

`QuotesApi` already had OpenTelemetry and `UseAzureMonitor` — since Day 5. The
worker and the outbox relay had none. Adding it to them is routine and took
twenty minutes. What took the day is that **even with all three instrumented,
a trace could not span them**, and the reason is in the architecture rather
than the configuration.

The chain is API → SQL outbox → relay → Service Bus → worker → SQLite. Day
20's transactional outbox is a store-and-forward boundary, and a trace does
not survive one by itself:

1. `POST /api/quotes` runs inside a trace, writes an `OutboxMessages` row, and
   returns. Its Activity ends.
2. Seconds later, a **different process** polls that table, finds the row, and
   publishes it. It has no ambient context, so it starts a brand-new trace.
3. The worker consumes and continues *that* trace.

The result is two unrelated operations that happen to be about the same quote.
The end-to-end view the outbox is supposed to be invisible to is exactly the
one it breaks — and nothing was misconfigured. The context simply had nowhere
to live between step 1 and step 2.

The `OutboxMessages` table had five columns and none of them was a trace id.

### The fix

A nullable `TraceParent` column, captured from `Activity.Current` when the row
is written, and restored as the **parent** when the relay publishes:

```csharp
// QuotesApi/Models/OutboxMessage.cs - the write side
TraceParent = System.Diagnostics.Activity.Current?.Id,
```

```csharp
// Quotes.Outbox/OutboxRelay.cs - the read side
using var activity = ActivitySource.StartActivity(
    "OutboxRelay.Publish",
    ActivityKind.Producer,
    parentId: row.TraceParent);
```

That `parentId` argument is the whole mechanism. The publish span lands inside
the *original request's* trace, the Azure Service Bus SDK injects whatever
context is current onto the outgoing message, and the consumer continues the
same trace. One line of behaviour, one column of storage.

Nullable in both senses, deliberately: rows written before the column existed
have no context, and a row written outside any trace legitimately has none.
Neither is an error and neither stops the relay publishing — a missing parent
costs the stitching, not the message.

## Proof

The query that settles it keeps only traces in which more than one service
took part. Before the change it could not have returned a row, because no
trace id was ever shared across the boundary:

```
$ az monitor app-insights query --app appi-jlwf2oyjdsjjg -g rg-thinkschool-dev2 \
    --analytics-query "union requests, dependencies | where timestamp > ago(30m)
      | summarize services = make_set(cloud_RoleName), spans = count(), started = min(timestamp)
        by operation_Id
      | where array_length(services) > 1"

operation_Id : 5e533d0d03d9a5e53210b8fa2f759d54
services     : ["QuotesApi", "Quotes.Outbox", "Quotes.Worker"]
spans        : 17
started      : 2026-09-09T06:55:41.8173448Z
```

One trace id. Three processes. Seventeen spans. In time order:

```
06:55:41.817  QuotesApi       request     POST /api/quotes/                    557.4ms
06:55:42.244  QuotesApi       dependency  sqlite                                16.8ms
06:55:42.323  QuotesApi       dependency  sqlite                                11.7ms
06:55:44.331  Quotes.Outbox   dependency  OutboxRelay.Publish                 4201.7ms   <-- restored parent
06:55:44.569  Quotes.Outbox   dependency  Message -> quote-events                0.8ms
06:55:44.577  Quotes.Outbox   dependency  ServiceBusSender.Send               3951.3ms
06:55:45.662  Quotes.Outbox   dependency  DefaultAzureCredential.GetToken     2358.4ms
06:55:45.829  Quotes.Outbox   dependency  GET 169.254.169.254 (IMDS)  FAILED    86.7ms
06:55:48.654  Quotes.Worker   request     ServiceBusProcessor.ProcessMessage  1169.5ms
06:55:48.654  Quotes.Worker   request     ServiceBusProcessor.ProcessMessage  1169.5ms   <-- fan-out
06:55:49.470  Quotes.Worker   dependency  sqlite                                47.9ms
06:55:49.540  Quotes.Worker   dependency  sqlite                                 0.5ms
06:55:49.576  Quotes.Worker   dependency  ServiceBusReceiver.Complete (audit)  214.9ms
06:55:49.583  Quotes.Worker   dependency  sqlite                                 1.3ms
06:55:49.603  Quotes.Worker   dependency  sqlite                                 0.6ms
06:55:49.604  Quotes.Worker   dependency  sqlite                                 0.2ms
06:55:49.606  Quotes.Worker   dependency  ServiceBusReceiver.Complete (search) 185.7ms
```

Two things in that listing are worth more than the stitch itself.

**The fan-out is visible, to the tick.** Two `ProcessMessage` spans at
`06:55:48.6545583` and `.6545584` — Day 19's topology showing up in telemetry
as one message delivered to two subscriptions. Across the whole run: 17
publishes, 34 consumes, exactly 2×.

**A 2.4-second credential acquisition, most of it spent failing.**
`DefaultAzureCredential.GetToken` took 2358ms, and immediately beside it is a
**failed** call to `169.254.169.254` — the Azure IMDS endpoint. The credential
chain probes managed identity first, waits for that to time out because this
is a laptop, then falls back to the CLI login. That failure is most of why the
first publish took 4.2 seconds. Later publishes average 54ms, because the
token is cached. Nobody would have found this by reading the code.

## The queries

All four in [`kql/`](kql/), with the reasoning in each file.

### p50/p99 by endpoint — [`kql/p50-p99-by-endpoint.kql`](kql/p50-p99-by-endpoint.kql)

```
cloud_RoleName  operation_Name                      calls  5xx  p50    p95    p99
QuotesApi       GET /api/quotes/                       32   30   11.7  290.5  664.9
Quotes.Worker   ServiceBusProcessor.ProcessMessage     34    0  119.7  303.5  321.3
QuotesApi       POST /api/quotes/                      17    0   68.3  183.6  183.6
```

p50 and p99 together, never p50 alone and never a mean. On the GET row the p50
is 11.7ms and the p99 is 664.9ms — a 57× spread that an average would have
reported as "about 90ms" and nobody would have looked twice.

(The 30 failures on that row are the malformed requests from Finding 4, before
the status-code fix. The window spans the fix, which is why they are still
counted as 5xx here.)

### Dependency breakdown — [`kql/dependency-breakdown.kql`](kql/dependency-breakdown.kql)

```
cloud_RoleName  type        target                                    calls  totalMs  p99
Quotes.Worker   servicebus  .../Subscriptions/search-indexer             17     1580  146.8
Quotes.Outbox   sqlite      Users | main                                366     1572  161.7
Quotes.Worker   servicebus  .../Subscriptions/audit-log                  17     1563  156.9
Quotes.Outbox   Other       OutboxRelay.Publish                          17      920  104.0
Quotes.Outbox   servicebus  .../quote-events                             34      909  102.2
QuotesApi       sqlite      Users | main                                 36      552  108.8
Quotes.Worker   sqlite      Users | main                                 85      217  164.6
```

Ranked by aggregate time, not by p99, because the slowest single call is
rarely the one worth fixing. That ordering immediately surfaces something no
individual span would: **366 SQLite calls from the relay** — a five-second
poll loop over thirty minutes, almost all of it finding nothing. It is the
largest source of database calls in the system and it is pure overhead. The
fix is a longer interval or a notification rather than a poll; the point here
is that the query found it without anyone suspecting it.

## The alert

Created and live:

```
$ az monitor scheduled-query show -g rg-thinkschool-dev2 -n quotes-api-error-rate
{ "name": "quotes-api-error-rate", "enabled": true, "severity": 2,
  "freq": "0:05:00", "window": "0:05:00" }
```

Two decisions in it worth defending.

**The threshold lives in the query, not in the rule.** The KQL returns rows
only when the rate is breached, and the rule is `count > 0`. The alternative —
a query returning a number, with the threshold configured in Azure — splits
the logic across two places, and the half sitting in the portal is the half
nobody reviews in a pull request.

**It counts 5xx, not `success == false`.** A 4xx is the caller's mistake, and
Day 26 demonstrated the cost of conflating them the hard way: 22 malformed
requests produced a 100% "error rate" on an endpoint that was working
correctly and rejecting them correctly. An alert that cannot tell "we are
broken" from "someone sent us rubbish" gets muted, and a muted alert is worse
than no alert because it still looks like coverage.

## Findings

Every one of these is a *silent* failure — a thing that produced no error, no
warning, and no missing-looking output. That is the pattern of the day, and it
is uncomfortable for a discipline whose entire job is making failures visible.

### Finding 1 — the outbox severed the trace, and nothing said so

Covered above. Worth restating as a general shape: **any store-and-forward
boundary drops trace context unless the context is stored with the message.**
An outbox, a job queue, a scheduled batch, a file drop. The instrumentation on
either side can be perfect and the trace still will not join, because the two
halves never shared an identifier. There is no error for this. There is just a
trace that stops, and another that starts, and no way to know they were the
same piece of work.

### Finding 2 — the App Insights resource had been deleted, and the config still looked fine

Local telemetry had been going nowhere. The connection string in user secrets
pointed at an instrumentation key in `centralindia`; the only App Insights
that exists in the subscription is `appi-jlwf2oyjdsjjg` in `southindia`:

```
$ az resource list --resource-type "microsoft.insights/components" -o table
Name                Rg                   Location
appi-jlwf2oyjdsjjg  rg-thinkschool-dev2  southindia
```

Every export was being rejected:

```
url.full: https://centralindia-0.in.applicationinsights.azure.com/v2.1/track
http.response.status_code: 400
StatusCode: Error
```

Deleting an App Insights resource does not invalidate the connection string
sitting in anyone's configuration. The SDK keeps POSTing, the endpoint keeps
returning 400, the application starts fine and reports nothing. The only
reason this surfaced is that the console exporter runs in Development and
printed the failed span — in Production it would have been perfectly silent.

`Program.cs` already carried a comment about the hardcoded OTLP endpoint
exporting "into nothing... no error, no log line, just no telemetry." Same
failure, different cause, two days apart.

### Finding 3 — `az monitor app-insights query -o table` prints nothing and exits 0

Three queries returned real data and displayed nothing:

```
$ az monitor app-insights query ... -o table
(no output, exit code 0)
```

The response nests rows inside `{tables:[{columns:[...],rows:[...]}]}`, and
the table formatter cannot flatten it. `--query "tables[0].rows" -o json`
works. A tool that returns nothing on success is indistinguishable from a
system with no data, which — on a day spent asking "is telemetry arriving?" —
is the worst possible ambiguity.

### Finding 4 — a client error reported as a server fault, inflating the alert's own signal

Sending `?page=1` without the required `size` parameter produced:

```
Microsoft.AspNetCore.Http.BadHttpRequestException:
Required parameter "int size" was not provided from query string.
```

`ExceptionHandlingMiddleware` caught it with everything else and returned
**500**. So ASP.NET Core's own report that the *caller* sent a bad request
became the server claiming the fault — and it landed squarely in the metric
Day 26 exists to alert on. Twenty-two typo'd query strings, one endpoint at a
100% server error rate, nothing actually wrong.

Fixed with a `BadHttpRequestException` branch returning 400 with the reason
(the caller can only fix what they are told), and two tests pinning it. The
existing theory covering four exception types still passes — 14/14 middleware
tests green.

Found by sending malformed requests by accident, which is a fair
approximation of how real clients find it.

### Finding 5 — the middleware swallowed exceptions before telemetry could see them

`exceptions | where timestamp > ago(30m)` returned `[]` while 22 of 22
requests were failing. The middleware handles every exception properly — logs
it with a stack trace, returns a clean problem document — and in doing so
ensures nothing propagates for the telemetry pipeline to observe. App Insights
recorded `success == false, resultCode 500` and nothing else. You could see
*that* the endpoint was broken; to learn *why* you had to find the one console
that happened to serve the request.

Two lines close it:

```csharp
activity?.AddException(ex);
activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
```

The span now carries the type, the message and the stack trace. The logging
was never the gap — the gap was that the log and the trace lived in different
places, so the trace could not answer the question the trace was for.

### Finding 6 — ranking dependencies by total time puts an idle consumer at the top

The first version of the dependency query ranked by aggregate duration, which
is correct reasoning and produced this:

```
Quotes.Worker  servicebus  .../search-indexer   20 calls   1,091,370 ms
Quotes.Worker  servicebus  .../audit-log        20 calls   1,091,304 ms
```

Eighteen minutes apiece, on a system that had handled one message. That is not
load — a message processor holds a long-poll receive open while waiting for
work, and the SDK reports the whole wait as dependency duration. An **idle**
consumer accrues the largest number on the page by doing nothing.

"Time spent working" and "time spent waiting to be given work" arrive in the
same column under the same name. The query now excludes
`ServiceBusReceiver.Receive*`, and says why at the point of exclusion, because
a future reader deleting that filter would get a plausible-looking answer that
is wrong.

### Finding 7 — the repo's own key-generation command silently produced a known key

Not a telemetry finding, but found today and fixed today, and it belongs with
the others because it has the identical shape.

Both `main.dev.bicepparam` and `main.prod.bicepparam` documented how to
generate the JWT signing key:

```powershell
$bytes = [byte[]]::new(48)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
azd env set JWT_SIGNING_KEY ([Convert]::ToBase64String($bytes))
```

The static `Fill()` is .NET Core only. It does not exist on the .NET Framework
that Windows PowerShell 5.1 runs on — which is the shell on this machine, and
the shell most people following these instructions will have. Run there, it
fails like this:

```
Method invocation failed because [System.Security.Cryptography.RandomNumberGenerator]
does not contain a method named 'Fill'.
...
Successfully saved Jwt:Key to the secret store.
```

Two lines, in that order. The `Fill()` call throws, `$bytes` stays all zeros
because `[byte[]]::new` zeroes it, and the very next line reports **success**
while storing the base64 of 48 zero bytes — a signing key anyone can reproduce
in one line, under a message saying it worked.

Both files now use `Create().GetBytes()`, which exists on .NET Framework and
.NET Core alike. Verified by comparing the stored key against
`[Convert]::ToBase64String([byte[]]::new(48))` rather than by reading it.

The reason this sits in Day 26 rather than Day 25: the same failure mode as
everything above. A security control that appears to work, reports that it
worked, and produces a known-value secret. Day 25 moved that key into Key
Vault and proved app settings held no plaintext; none of that helps if the
key going into the vault is forty-eight zeroes.

## Files

| File | What it is |
|---|---|
| [`kql/p50-p99-by-endpoint.kql`](kql/p50-p99-by-endpoint.kql) | Latency percentiles and error rate per endpoint, split by service. |
| [`kql/dependency-breakdown.kql`](kql/dependency-breakdown.kql) | Where time goes, ranked by aggregate cost, with the receive loop excluded (Finding 6). |
| [`kql/error-rate-alert.kql`](kql/error-rate-alert.kql) | The alert query. Returns rows only on breach; 5xx only. |
| [`kql/distributed-trace-check.kql`](kql/distributed-trace-check.kql) | Asserts the stitch: traces containing both `QuotesApi` and `Quotes.Worker`. Returns nothing if the outbox drops context again. |
| `QuotesApi/Models/OutboxMessage.cs` | `TraceParent`, captured at write time. |
| `QuotesApi/Data/QuotesDbContext.cs` | The column, 55 chars — the W3C traceparent's fixed length. |
| `QuotesApi/Middleware/ExceptionHandlingMiddleware.cs` | 400 for malformed requests (Finding 4); exceptions recorded on the span (Finding 5). |
| `Quotes.Outbox/OutboxRelay.cs` | The publish span, parented to the stored traceparent. |
| `Quotes.Outbox/Program.cs`, `Quotes.Worker/Program.cs` | OpenTelemetry with the Azure Monitor exporter and `AddSource("Azure.*")`. |
| `sql/add-outbox-traceparent.sql` | The hand-run migration for the live Azure SQL database, following Day 20's pattern. |
| `infra/main.dev.bicepparam`, `infra/main.prod.bicepparam` | Key-generation snippet fixed - `Create().GetBytes()`, not the .NET-Core-only static `Fill()` (Finding 7). |

`AddSource("Azure.*")` is the line worth noticing in those two Program.cs
files. It is what captures the Service Bus SDK's own spans — the ones that
carry context onto and off the wire. Without it both processes report their
own work correctly and stitch to nothing, which is the most misleading
possible half-success.

## A note on the live database

`sql/add-outbox-traceparent.sql` must run before Day 26's code is deployed.
The live Azure SQL database uses `EnsureCreated()`, which is a no-op against
an existing schema, so the `AddOutboxTraceParent` migration will never reach
it (Days 20 and 24 Finding 17). Deploying without it makes every
`POST /api/quotes` fail on `Invalid column name 'TraceParent'`. The script is
idempotent and matches both migrations column-for-column.

## GitHub link

https://github.com/thinkbridge-thinkschool/Thinkbridge_Shruti_Sahrawat/tree/main/Days/day-26

Commit `24a54f8`.

## What did you learn this session?

<!-- one line, in your own words -->

## What would break this?

**Trace context is now a schema concern.** Anything that writes an outbox row
outside a request — a backfill, an admin script, a future scheduled job — gets
a null `TraceParent` and publishes an unstitched trace. That is correct
behaviour and it is also a silent degradation: the message still flows, the
trace just quietly stops being end-to-end, and only
`distributed-trace-check.kql` returning fewer rows would show it.

**The relay's 366 polls per half hour are unaddressed.** Finding 6's query
surfaced them; nothing was done about them. At this volume it is invisible; at
production volume a five-second poll against a growing table is the kind of
cost that gets discovered as a database bill.

**Nothing tests the stitching.** The trace was verified by hand, once. There is
no test that fails if someone removes the `parentId` argument, and the failure
mode is invisible — messages keep flowing, traces keep being recorded, they
just stop being the same trace. A test asserting that an outbox row round-trips
its traceparent would be cheap and is not written.

**The alert has no action group.** It fires and shows in the portal, and
notifies nobody. That is a deliberate stopping point for an exercise, not a
working alert.

**The 2.4-second credential probe is diagnosed, not fixed.** `DefaultAzureCredential`
probing IMDS on a laptop costs ~2.4s on first token acquisition. Excluding
`ManagedIdentityCredential` locally would remove it. Left alone because the
production path is the one that should be fast, and there it is the *only*
credential in the chain.
