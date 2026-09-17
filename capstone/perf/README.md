# Capstone perf — the publish path under load

The hot path is `POST /api/collections/{id}/publish`: the one endpoint whose
shape is a design decision rather than a routing detail, and the one the whole
outbox exists to keep fast.

- Load script: [`publish-load-test.js`](publish-load-test.js)
- Measured: 10 virtual users, 60 seconds, 20 followers per curator
- Build: Release, SQLite file, one Windows laptop, API and load generator on
  the same machine

```powershell
# from capstone/src/Capstone.Api, with a database that does not exist yet
Remove-Item capstone-curation.db*
dotnet ef database update --project ..\Modules\Curation\Capstone.Curation.Infrastructure --startup-project .
dotnet run --configuration Release
```

```powershell
# from capstone/perf
k6 run --env BASE=http://localhost:5000 publish-load-test.js
```

Start from an empty database every time. The outbox is never pruned — that is
what a non-destructive drain means — so a run against yesterday's file is
measuring a different table from the one the last run measured. The script's
first line prints `2 bytes` when the reset worked; anything larger and the run
is not comparable.

## The numbers

| | p50 | p90 | p95 | p99 | max | iterations/s | delivered in 60s |
|---|---:|---:|---:|---:|---:|---:|---:|
| **Before** | 2.00 ms | 5.29 ms | **153.73 ms** | **614.24 ms** | 3.55 s | 146 | 1,576 of 8,865 |
| Relay parked *(diagnostic)* | 1.99 ms | 4.03 ms | 7.80 ms | 463.25 ms | 2.16 s | 171 | 0 of 10,421 |
| **After** | 1.76 ms | 3.87 ms | **8.50 ms** | **465.46 ms** | 2.17 s | 170 | 3,126 of 10,278 |

An earlier run of the same configuration, before the script counted the
backlog, gave p95 158.38 ms and p99 615.84 ms. Two independent measurements of
the unfixed code 1.6 ms apart at p99 is the reason the tail is treated here as
a property rather than as noise — which matters, because throughput between
those same two runs differed by 25% (7,092 against 8,865 iterations on
identical settings). Percentiles reproduced; throughput did not. Only the
percentiles are load-bearing below.

## What was wrong

`EfOutboxStore.MarkSentAsync` took one message id and spent two round trips on
it: a `FirstOrDefaultAsync` to load the row, a guard against it already being
stamped, then `SaveChangesAsync`. `InProcessRelay` called it once per delivered
message inside the drain loop, so a twenty-row batch cost twenty reads and
twenty separate write transactions.

SQLite permits one writer at a time. Every one of those twenty transactions
queued alongside the request traffic, four times a second, forever.

## How that was attributed rather than assumed

The poll interval became configuration earlier the same day, for the test host's
benefit — `Relay:PollIntervalMilliseconds`, so an endpoint test could park the
relay and assert that a row was still unsent without racing a background thread.
That made a second run possible at no cost: the identical load with the relay
asleep.

```powershell
$env:Relay__PollIntervalMilliseconds="3600000"
```

**p95 fell from 153.73 ms to 7.80 ms — 20× — with no code changed.** The relay
owned essentially the whole p95 tail.

**p99 fell only from 614.24 ms to 463.25 ms.** So roughly a quarter of the
extreme tail was the relay and three quarters was something else: the API
contending with itself. That run did 171 iterations a second and each iteration
writes three times, which is over 500 write transactions a second against a
single SQLite file with no relay involved at all.

Without that middle run the after-numbers below would have read as a complete
fix. They are not one, and the diagnostic is the only reason that is known.

## The change

One statement per drain instead of two per row.

```csharp
await db.OutboxMessages
    .Where(message => messageIds.Contains(message.MessageId) && message.SentAt == null)
    .ExecuteUpdateAsync(
        setters => setters.SetProperty(message => message.SentAt, sentAt),
        cancellationToken);
```

`IOutboxStore.MarkSentAsync` takes a set rather than one id. `InProcessRelay`
collects the ids it has delivered and acknowledges them in a `finally`, so the
ordering the design rests on is untouched — nothing is stamped before a
subscriber has accepted it — while the write transactions per drain go from
twenty to one.

The guard moved into the `WHERE` clause rather than disappearing:
`SentAt == null` does what the `if` did, and a row somebody already stamped
keeps the timestamp of the delivery that happened rather than the retry that
noticed.

**After: p95 8.50 ms, p99 465.46 ms** — against the parked relay's 7.80 ms and
463.25 ms. At every percentile the fixed relay costs what switching the relay
off costs. That is the strongest form the claim can take, and it is also the
ceiling: p99 cannot improve further without addressing the API's own write
contention, which this change does not touch.

### What it trades away

A process that dies mid-batch used to have acknowledged the messages it had
already delivered. Now it has not, so the whole batch redelivers. That is still
at-least-once, it is absorbed by the same `(MessageId, consumer)` deduplication
that was always required, and it is a wider window than before. Recorded here
because a redelivery window that nobody wrote down is a duplicate feed entry
that nobody can explain.

## What is still wrong, and was not fixed

**The relay cannot keep up, and the fix did not change that.** Delivery went
from 1,576 to 3,126 messages in sixty seconds — 52 a second against a publish
rate of 170 — and 7,152 rows were still unsent when the load stopped. The
acknowledge cost is gone; the sleep is not. `BatchSize` 20 every 250 ms caps the
relay at 80 messages a second whatever else is true, so above that rate the
outbox stops being a buffer that absorbs a burst and becomes a queue that only
drains once the burst ends.

Raising the batch size or shortening the interval moves the number without
changing the shape. The shape is the problem: a table asked every quarter
second is the wrong mechanism, and day 3 of the build plan replaces it with a
broker that is pushed to rather than polled. Left as a measured ceiling rather
than tuned into looking fine.

**`GET /api/outbox` reads the whole table and has no bound.** Predicted to be a
latency problem; it is not, at this size. Same request, same code, against an
empty table and then against one holding 10,278 rows:

| rows | time | body |
|---:|---:|---:|
| 0 | 22.5 ms | 2 bytes |
| 10,278 | 45.0 ms | 1,952,634 bytes |

The body grew by a factor of a million and the time doubled. So the defect is
real and it is a payload problem, not a latency one, at ten thousand rows — and
the honest note is that this would have been written up as a latency bug if it
had been reasoned about instead of measured. It still needs a bound, because
nothing prunes the table and 10,278 rows is one minute of one load test.

**The API's own write contention is the whole remaining p99.** 465 ms at p99
with the relay contributing nothing. Three writes per iteration against a single
SQLite file is the mechanism; the fix is not a smaller query, it is either fewer
writes per operation or a store that admits more than one writer — which is what
`infra/modules/sql.bicep` already provisions for the deployed shape. Not
investigated further today; named so it is not rediscovered as a surprise.

## The thresholds, and why they are where they are

```js
thresholds: {
  publish_duration: ['p(95)<30', 'p(99)<750'],
  checks: ['rate>0.99'],
}
```

p95 is the guard that matters. Measured at 8.50 ms, and the regression this day
fixed took it to 153.73 ms — so a 30 ms line catches a return of that specific
mistake with room for a slower machine, and catches it long before p99 would
notice.

p99 is a gross-regression guard only, deliberately loose. The floor is 465 ms
and it is caused by contention this change did not address, so a tight line
there would fail every run and be switched off within a week. A threshold that
is always red is not a threshold.

Both numbers are from one laptop with the load generator on the same machine as
the API. They are the right shape for catching a regression on that laptop and
the wrong numbers to quote as the capacity of anything.
