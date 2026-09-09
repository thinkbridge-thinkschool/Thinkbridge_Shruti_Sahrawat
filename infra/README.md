# infra/ — the Quotes stack as Bicep

Everything in this folder describes infrastructure. Nothing in it is applied
automatically: the CI job (`.github/workflows/infra.yml`) builds and lints the
templates and never touches a subscription, and a deployment only happens when
somebody runs one of the commands below.

| File | What it is |
|---|---|
| [`main.bicep`](main.bicep) | Subscription-scoped entry point. Creates the resource group, then calls every module in dependency order. |
| [`types.bicep`](types.bicep) | Shared user-defined types, so a malformed parameter file fails at `bicep build` instead of halfway through a deployment. |
| [`modules/identity.bicep`](modules/identity.bicep) | The one user-assigned managed identity everything authenticates as. |
| [`modules/sql.bicep`](modules/sql.bicep) | Azure SQL logical server + database, Entra-ID-only authentication. |
| [`modules/servicebus.bicep`](modules/servicebus.bicep) | Namespace, the `quote-events` topic, its subscriptions and filters, and the data-plane role assignments. |
| [`modules/api.bicep`](modules/api.bicep) | The API container app, its managed environment and its Log Analytics workspace. |
| [`modules/registry-access.bicep`](modules/registry-access.bicep) | AcrPull on a registry outside this stack. Deployed only when a registry resource ID is supplied. |
| [`main.dev.bicepparam`](main.dev.bicepparam) | dev values. Cheap, disposable, scales to zero. |
| [`main.prod.bicepparam`](main.prod.bicepparam) | prod values. Valid and type-checked; never deployed — see the header in that file. |
| [`bicepconfig.json`](bicepconfig.json) | Linter settings. Most rules raised from warning to error. |
| [`scripts/create-sql-user.sql`](scripts/create-sql-user.sql) | The one step Bicep cannot express: the managed identity's database user. |
| [`azure.yaml`](azure.yaml) | Day 24 - a separate azd project scoped to this folder, wrapping this same template in an Azure Deployment Stack. |
| [`scripts/azd-provision.ps1`](scripts/azd-provision.ps1) | Day 24 - the entry point. Selects the environment's `.bicepparam` before azd resolves parameters, then calls `azd provision` - a `preprovision` hook alone runs one step too late (see `Days/day-24/README.md`). |
| [`scripts/select-bicepparam.ps1`](scripts/select-bicepparam.ps1) | Day 24 - copies the active environment's `.bicepparam` onto `main.bicepparam`. Called by `azd-provision.ps1`; also wired as azd's `preprovision` hook, as a re-assertion rather than the mechanism. |

## Before you run anything

Two values come from your own directory rather than from this repo, so no
directory object ID is committed here:

```powershell
$env:SQL_AAD_ADMIN_LOGIN     = az ad signed-in-user show --query userPrincipalName -o tsv
$env:SQL_AAD_ADMIN_OBJECT_ID = az ad signed-in-user show --query id -o tsv
```

If either is unset, the build fails with `BCP333` before reaching Azure —
`sqlAadAdminObjectId` is constrained to 36 characters precisely so an unset
variable cannot become a deployment with a broken administrator.

## Plan (changes nothing)

```powershell
az deployment sub what-if `
  --name quotes-dev-plan `
  --location southindia `
  --template-file infra/main.bicep `
  --parameters infra/main.dev.bicepparam
```

`what-if` creates nothing and costs nothing. If it errors with
`ResourceGroupNotFound`, create the (empty, free) group first and re-run —
what-if evaluates nested group-scoped deployments against a group that has to
exist:

```powershell
az group create --name rg-quotes-dev --location southindia
```

## Deploy

```powershell
az deployment sub create `
  --name quotes-dev `
  --location southindia `
  --template-file infra/main.bicep `
  --parameters infra/main.dev.bicepparam
```

Then the step that is not Bicep — the managed identity's database user:

```powershell
$outputs = az deployment sub show --name quotes-dev --query properties.outputs -o json | ConvertFrom-Json
sqlcmd -S $outputs.sqlServerFqdn.value -d $outputs.sqlDatabaseName.value -G `
  -v identityName=$($outputs.managedIdentityName.value) `
  -i infra/scripts/create-sql-user.sql
```

Then push the real image and point the app at it:

```powershell
az deployment sub create `
  --name quotes-dev `
  --location southindia `
  --template-file infra/main.bicep `
  --parameters infra/main.dev.bicepparam `
  --parameters apiContainerImage=<registry>/quotes-api:<tag> `
               containerRegistryLoginServer=<registry> `
               containerRegistryResourceId=<full registry resource ID>
```

Supplying `containerRegistryResourceId` is what grants this stack's identity
AcrPull on a registry it does not own — without it the container app is created
pointing at an image it cannot pull, and reports that fact long after the
deployment says it succeeded.

## Confirming the filter actually filters

The `$Default` rule on a subscription holds a TrueFilter that matches
everything. `modules/servicebus.bicep` overwrites it rather than adding a
second rule beside it, because Service Bus ORs rules together — a filter added
alongside `$Default` changes nothing at all and looks completely correct in the
portal. Worth confirming after a deploy rather than trusting the template:

```powershell
az servicebus topic subscription rule list `
  --resource-group rg-quotes-dev `
  --namespace-name <namespace> `
  --topic-name quote-events `
  --subscription-name search-indexer `
  -o table
```

One rule named `$Default`, `filterType` of `SqlFilter`, and the expression
`eventType = 'QuoteCreated'`. Two rules — or one named `$Default` with a
TrueFilter — means the indexer is receiving every event.

## What this template does not do

- **It does not adopt the existing `rg-thinkschool-dev2` resources.** Those were
  created by `azd` under generated names. Pointing these parameters at them
  (`resourceGroupName`, `sqlServerName`, `serviceBusNamespaceName`,
  `apiName`) makes `what-if` an adoption report — useful, and the honest way to
  find out what adoption would change — but running the *deployment* would
  overwrite properties `azd` owns, including rolling the container app back to
  whatever image the parameters name. Plan first; do not deploy into that group
  without reading the plan line by line.
- **It does not switch the existing database to `Migrate`.** The dev parameters
  set `Database:SchemaBootstrap=Migrate`, which is correct for a database this
  template creates: it starts empty and every migration, `AddOutbox` included,
  applies cleanly. The `azd`-created database was bootstrapped with
  `EnsureCreated()` and therefore has no `__EFMigrationsHistory` table, so the
  first migration there would try to create tables that already exist. That
  database stays on `EnsureCreated` plus [`sql/add-outbox-table.sql`](../sql/add-outbox-table.sql);
  see Day 20.
- **It does not create the Static Web App.** Day 17's frontend has its own
  deploy path (`.github/workflows/deploy-swa.yml`) and a region list that does
  not include `southindia`. Folding it in here would mean a template whose
  location parameter is a lie for one of its resources.
- **It does not put SQL behind a private endpoint.** `sqlAllowAzureServices`
  opens the `0.0.0.0` rule, which admits Azure traffic from *any* tenant, not
  just this one. The real fix is VNet integration for the container app plus a
  private endpoint on the server, and it is a larger change than this exercise
  covers — named here rather than left as a comfortable default.

## Running this through azd instead (Day 24)

Everything above still works exactly as written - nothing in `main.bicep`
or either `.bicepparam` file changed for this. What's new is a second way to
run the same template: through `azd`, wrapped in an Azure Deployment Stack,
from a **second azure.yaml that lives inside this folder**
(`infra/azure.yaml`), not the one at the repo root - which drives the real,
live app in `rg-thinkschool-dev2` and is never read when `azd` runs from
inside `infra/`. Always `cd infra` first.

```powershell
azd config set alpha.deployment.stacks on   # one-time per machine
cd infra
azd env new dev --location centralindia
azd env set SQL_AAD_ADMIN_LOGIN     (az ad signed-in-user show --query userPrincipalName -o tsv)
azd env set SQL_AAD_ADMIN_OBJECT_ID (az ad signed-in-user show --query id -o tsv)

.\scripts\azd-provision.ps1 -Environment dev -ReuseManagedEnvironment
```

`centralindia`, not `southindia`: South India will not provision a *new*
Azure SQL server for this subscription at all (`ProvisioningDisabled`), which
Day 23's `what-if` had no way to catch because what-if never asks a region
whether it has room.

`-ReuseManagedEnvironment` is what makes the deployment reach the end. This
subscription permits exactly one Container Apps managed environment and the
live `quotes-api` already holds it, so a stack that creates its own fails at
`MaxNumberOfGlobalEnvironmentsInSubExceeded` *after* SQL and Service Bus are
already standing. The switch discovers the environment that exists
(`az containerapp env list`) and hands azd two values the parameter files
read: `EXISTING_CONTAINERAPP_ENV_ID`, and `API_LOCATION` — because a container
app must sit in its environment's region, so the app lands in `southindia`
beside the environment it borrows while the rest of the stack stays in
`centralindia`. That cross-region app-to-database hop is a genuine latency
cost, chosen over not deploying at all.

The app is named `quotes-api-dev` / `quotes-api-prod` in the parameter
files, not the template's `quotes-api` default, and that is not cosmetic: a
container app name must be unique within its *managed environment*, and the
live app in the environment being borrowed is called exactly `quotes-api`.
A different resource group does not separate them — the hostname would
collide with the one the Static Web App proxies `/api/*` to. See
[`Days/day-24/README.md`](../Days/day-24/README.md), Finding 8.

The wrapper also refuses to reuse an environment that lives inside this
stack's own resource group: `actionOnUnmanage.resourceGroups: delete` is not
resource-scoped, so `azd down` would take it down with the group despite the
stack never managing it — and being unmanaged is exactly why `denyDelete`
would not stop that.

What the stack still owns, borrowed environment or not: the resource group,
the managed identity, the SQL server and database, the Service Bus namespace
and its topology, and the container app itself. The environment is the single
piece it borrows, and it is deliberately **not** declared as an `existing`
resource — `denySettings` and `azd down` apply to what a stack manages, and a
stack entitled to delete the live app's environment is a worse outcome than
the quota.

Drop the switch on any subscription with room and the template creates its own
environment and workspace exactly as Day 23 did — that path is unchanged and
still the default.

Two things must be set before this will start, both learned the hard way
(Days/day-24, Findings 16 and 17):

```powershell
# 1. A JWT signing key. No default - QuotesApi refuses to start in Production
#    without it, and this template sets Production in every environment.
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$bytes = New-Object byte[] 48; $rng.GetBytes($bytes)
azd env set JWT_SIGNING_KEY ([Convert]::ToBase64String($bytes))

# 2. The managed identity's database user - the one step that is not Bicep.
#    Needs a temporary firewall rule for the operator's IP; the template
#    deliberately has none, because the app does not need one.
$myIp = (Invoke-RestMethod https://api.ipify.org)
az sql server firewall-rule create -g rg-quotes-dev -s <sql server> `
  -n temp-operator --start-ip-address $myIp --end-ip-address $myIp
sqlcmd -S <sql server>.database.windows.net -d quotesdb -G `
  -v identityName="quotes-id-dev" -i scripts/create-sql-user.sql
```

Then push the real image and point the app at it:

```powershell
az acr login --name <registry>
dotnet publish ../QuotesApi/QuotesApi.csproj -c Release /t:PublishContainer `
  -p:ContainerRegistry=<registry>.azurecr.io
azd env set API_CONTAINER_IMAGE "<registry>.azurecr.io/quotes-api:0.1.0"
azd env set ACR_LOGIN_SERVER    "<registry>.azurecr.io"
azd env set ACR_RESOURCE_ID     (az acr show -n <registry> --query id -o tsv)
```

`ACR_RESOURCE_ID` is what grants this stack's identity AcrPull on a registry
it does not own. Verified working: the container app pulled
`quotes-api:0.1.0` in 192ms from a registry in another resource group.

Confirm the app actually started - `provisioned` and `running` are different
claims, and azd only reports the first:

```powershell
az containerapp show -n quotes-api-dev -g rg-quotes-dev `
  --query "{latest:properties.latestRevisionName, ready:properties.latestReadyRevisionName}"
# those two must MATCH. If they differ, the revision never passed its probes
# and the previous revision is still serving traffic.
curl.exe https://<fqdn>/health   # expect: Healthy
```

Verify the result against Azure rather than against azd's exit message. With
`alpha.deployment.stacks` off — which the prod plan below requires — all of
this still deploys and still prints `SUCCESS`, as a plain deployment, with
this project's whole `deploymentStacks` block silently ignored:

```powershell
azd config get alpha.deployment.stacks   # expect "on"
az stack sub list -o table               # expect: azd-stack-dev  succeeded
az resource list -g rg-quotes-dev -o table
```

The deployed dev stack, for reference — note the container app's region, and
the absence of a Log Analytics workspace, both consequences of borrowing the
environment:

| Resource | Region |
|---|---|
| `quotes-id-dev` | centralindia |
| `quotes-sb-dev-<token>` | centralindia |
| `quotes-sql-dev-<token>` + `quotesdb` | centralindia |
| `quotes-api-dev` | **southindia** (its environment's region) |

`azd-provision.ps1`, not `azd provision` directly - the parameters file
(`main.bicepparam`) has to exist before azd resolves parameters, one step
earlier than a `preprovision` hook runs. Skipping the wrapper doesn't just
risk an interactive prompt (missing file) - it risks silently planning a
*stale* file with no warning at all if one happens to already be on disk
(found by actually hitting it - see
[`Days/day-24/README.md`](../Days/day-24/README.md), Finding 5). That file
has the full run for both environments: dev's identity, Service Bus and SQL
deployed and were tracked as a genuine stack before hitting a
subscription-wide Container Apps quota this exercise deliberately doesn't
work around; prod's real plan (5 resources, the same
`NestedDeploymentShortCircuited` diagnostics Day 23 found); and the
sharpest single finding across both days - `azd down` reporting success
while deleting nothing, when no stack yet existed to enumerate, versus
genuinely tearing down three resources once one did.

## Tearing this stack down

```powershell
cd infra
azd down --force --purge
```

Then check the vault actually went, because soft-delete reserves its name even
after the resource group is gone, and this stack's names are deterministic -
so a vault left soft-deleted collides with itself on the next deploy of the
same environment (Days/day-25, Finding 5):

```powershell
az keyvault list-deleted --query "[?starts_with(name, 'kv-')].{name:name, purgeOn:properties.scheduledPurgeDate}" -o table
az keyvault purge --name <name> --location <region>   # if anything is listed
```
