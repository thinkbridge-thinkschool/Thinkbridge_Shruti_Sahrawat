# Day 19 — running against real Azure instead of the emulator

The local Docker emulator (`servicebus-emulator/`) is still in the repo and
still the documented approach — see [`servicebus-emulator/README.md`](../../servicebus-emulator/README.md)
for why it exists. The actual verification run for this submission used a real
Azure Service Bus **Standard** namespace instead, because the emulator's first
pull (~1.5 GB) stalled on a slow connection for over 20 minutes with nothing to
show for it, and switching to Azure avoided the download entirely.

## What's different

`Quotes.Worker/appsettings.json` has `FullyQualifiedNamespace` set to the real
namespace and `ConnectionString` left empty. That's not a fallback path bolted
on for this — [`ServiceBusClientFactory`](../../Quotes.Messaging/Publishing/ServiceBusClientFactory.cs)
already preferred namespace + `DefaultAzureCredential` over a connection string
whenever both are available; today is just the first time that branch actually
ran. Locally, `DefaultAzureCredential` resolves to `AzureCliCredential` - your
own `az login` session - so no code changed, only configuration.

## One-time setup this required

```powershell
az servicebus namespace create --name sb-quotes2-qvdk5l --resource-group rg-thinkschool-dev2 --location centralindia --sku Standard
az servicebus topic create --name quote-events --namespace-name sb-quotes2-qvdk5l --resource-group rg-thinkschool-dev2
az servicebus topic subscription create --name search-indexer --topic-name quote-events --namespace-name sb-quotes2-qvdk5l --resource-group rg-thinkschool-dev2 --max-delivery-count 3
az servicebus topic subscription create --name audit-log --topic-name quote-events --namespace-name sb-quotes2-qvdk5l --resource-group rg-thinkschool-dev2 --max-delivery-count 3
az servicebus topic subscription rule create --name only-quote-created --namespace-name sb-quotes2-qvdk5l --resource-group rg-thinkschool-dev2 --topic-name quote-events --subscription-name search-indexer --filter-sql-expression "eventType = 'QuoteCreated'"
```

Plus one role assignment, granting the signed-in `az` account data-plane rights
on the namespace (RBAC is separate from the Azure SQL role grants from
yesterday - a different resource type, a different permission model):

```powershell
$principalId = az ad signed-in-user show --query id -o tsv
$namespaceId = az servicebus namespace show --name sb-quotes2-qvdk5l --resource-group rg-thinkschool-dev2 --query id -o tsv
az role assignment create --assignee $principalId --role "Azure Service Bus Data Owner" --scope $namespaceId
```

`Data Owner` rather than separately-scoped Sender/Receiver roles because this
one account needs to both publish and consume for the demo; a deployed
production worker would get only the roles it actually uses.

## Cost and cleanup

Standard tier bills a small hourly base charge - not free like the emulator,
which is why the emulator remains the documented default for anyone re-running
this without wanting to spend anything. Delete the namespace once evidence is
captured:

```powershell
az servicebus namespace delete --name sb-quotes2-qvdk5l --resource-group rg-thinkschool-dev2
```
