# Local Service Bus emulator

Microsoft's [Service Bus emulator](https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator)
in Docker, configured with the topology Day 19 needs.

## Why this exists

Service Bus **topics do not exist below the Standard tier** — Basic supports
queues only. So "publish to a topic" cannot be done on a free namespace at all,
and a paid one carries a monthly base charge. The emulator serves the same AMQP
surface — subscriptions, SQL filters, delivery counts, dead-letter queues — for
nothing.

## Topology

Defined in [`config.json`](config.json), applied at container start:

| Entity | Type | Notes |
|---|---|---|
| `quote-events` | topic | duplicate detection deliberately **off** — see below |
| `quote-events/search-indexer` | subscription | SQL filter `user.eventType = 'QuoteCreated'`, `MaxDeliveryCount` 3 |
| `quote-events/audit-log` | subscription | catch-all rule `1=1`, `MaxDeliveryCount` 3 |

`MaxDeliveryCount` is 3 rather than the Azure default of 10 so that a message
that fails every delivery reaches the dead-letter queue in seconds.

Duplicate detection is off on purpose. With it on, the broker would silently
swallow a replayed message and the consumer-side idempotency ledger — the thing
Day 19 is actually about — would never be exercised.

## Running it

```powershell
.\start.ps1          # generates .env with a random local-only password, then docker compose up
.\stop.ps1           # docker compose down
.\stop.ps1 -RemoveVolumes   # also drops the SQL state, resetting the topic to empty
```

First run pulls roughly 1.5 GB of images.

## Connecting

```
Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;
```

`SAS_KEY_VALUE` is the fixed literal the emulator expects, not a credential —
`UseDevelopmentEmulator=true` makes the client ignore the key entirely.

Against real Azure this connection string would be replaced by a
`FullyQualifiedNamespace` and `DefaultAzureCredential`, so the worker
authenticates with the same user-assigned managed identity the API already uses
for Azure SQL and no key exists at all.
[`ServiceBusClientFactory`](../Quotes.Messaging/Publishing/ServiceBusClientFactory.cs)
prefers that path whenever a namespace is configured.

## Limitations worth knowing

- State does not survive `docker compose down -v`; entities are recreated from
  `config.json` on every start.
- No partitioned entities, no JMS, AMQP over TCP only (no WebSockets).
- 256 KB max message size, 100 MB max entity size.
- Development only — no SLA, not for production.
