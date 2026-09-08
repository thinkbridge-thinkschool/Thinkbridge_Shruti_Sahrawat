# Day 24 — Deployment Stacks + azd

Deploys the Day 23 stack (`infra/main.bicep`) through `azd` instead of raw
`az deployment sub` commands, wrapped in an Azure Deployment Stack. Dev was
run for real against the actual subscription; prod is planned. Thirteen findings came out of it.

**The end state: the full stack is deployed, as a real Deployment Stack.**

```
$ az stack sub list -o table
Name           State      Last Modified
-------------  ---------  --------------------------------
azd-stack-dev  succeeded  2026-09-08T13:34:47.990063+00:00
```

```
$ az resource list -g rg-quotes-dev -o table
Name                                   Location      Type
-------------------------------------  ------------  ------------------------------------------------
quotes-id-dev                          centralindia  Microsoft.ManagedIdentity/userAssignedIdentities
quotes-sb-dev-e6oljhc2krrhe            centralindia  Microsoft.ServiceBus/namespaces
quotes-sql-dev-e6oljhc2krrhe           centralindia  Microsoft.Sql/servers
quotes-sql-dev-e6oljhc2krrhe/quotesdb  centralindia  Microsoft.Sql/servers/databases
quotes-api-dev                         southindia    Microsoft.App/containerApps
```

Four things in that output are the whole exercise, and none of them were
true a day earlier:

* **`quotes-api-dev` exists.** The deployment used to die before reaching it
  (Finding 4). It now runs in the managed environment that already existed,
  in a *different resource group*, proving cross-resource-group environment
  joining works (Finding 7).
* **It is in `southindia` while everything else is in `centralindia`** — the
  region split `apiLocation` exists for, because the borrowed environment's
  region cannot host a new Azure SQL server on this subscription (Finding 2).
* **There is no Log Analytics workspace in the group.** Correct, and not an
  omission: log destination belongs to the environment, so a workspace here
  would have been an empty resource impersonating observability (Finding 7).
* **The name is `quotes-api-dev`, not `quotes-api`.** The live app in that
  same environment *is* `quotes-api`, on
  `quotes-api.blacksand-b575aaa0.southindia.azurecontainerapps.io`; this one
  is `quotes-api-dev.blacksand-b575aaa0.southindia.azurecontainerapps.io`.
  Same environment domain. Identical names would have contended for the
  identical hostname — the one Day 17's Static Web App proxies `/api/*` to
  (Finding 8).

The last of them changed the template. Dev's first real run reached a
genuine, subscription-wide ceiling — this subscription permits exactly one
Container Apps managed environment and the live `quotes-api` already holds
it — after identity, Service Bus and SQL had all deployed and been tracked
as a real stack. The first version of this write-up argued that was a
complete result. It isn't quite: the exercise says *deploy the full stack*,
and "the subscription is full" is a reason the stack can't be created a
second time, not a reason the app can't be deployed. Finding 7 is the
change that closes it — the container app can now join the environment that
already exists instead of demanding its own, which is both the only way this
stack deploys end to end here and, separately, how a real environment gets
shared between apps anyway.

## What's new

| File | What it is |
|---|---|
| [`infra/azure.yaml`](../../infra/azure.yaml) | A **second, separate** azd project living inside `infra/` — deliberately not the repo-root `azure.yaml`, which drives the real, live app in `rg-thinkschool-dev2`. `azd` resolves to whichever `azure.yaml` is nearest to the current directory, so everything below only ever runs from inside `infra/`. |
| [`infra/scripts/azd-provision.ps1`](../../infra/scripts/azd-provision.ps1) | The entry point. Selects the right `.bicepparam`, then calls `azd`. See "Why not just a hook" below — this exists because the more obvious design (a preprovision hook alone) doesn't work. |
| [`infra/scripts/select-bicepparam.ps1`](../../infra/scripts/select-bicepparam.ps1) | Copies `main.dev.bicepparam` or `main.prod.bicepparam` onto `main.bicepparam` — the one filename azd's Bicep provider actually reads. Called directly by `azd-provision.ps1`, and also wired as azd's `preprovision` hook as a re-assertion (see below for why it can't be the only mechanism). |
| `infra/main.bicepparam` (generated, gitignored) | Never hand-edited. Whichever of the two real files was last selected. |
| `existingManagedEnvironmentId` + `apiLocation` in [`main.bicep`](../../infra/main.bicep) / [`modules/api.bicep`](../../infra/modules/api.bicep) | Lets the container app join a managed environment the stack does not own. Empty (the default) is Day 23's exact behaviour. Finding 7. |
| `-ReuseManagedEnvironment` in [`azd-provision.ps1`](../../infra/scripts/azd-provision.ps1) | Discovers that environment with `az containerapp env list` and sets both values, rather than having anyone paste a subscription-scoped resource ID. |

`types.bicep`, `identity.bicep`, `sql.bicep`, `servicebus.bicep` and
`registry-access.bicep` are byte-for-byte unchanged from Day 23. Three
files changed, each for a failure this run actually hit: `location` in
`main.dev.bicepparam` and `main.prod.bicepparam` (Finding 2), and the
optional managed-environment reuse in `main.bicep` and `modules/api.bicep`
(Finding 7). Every default is Day 23's default, so a subscription with room
gets Day 23's deployment unchanged.

## Why a second azure.yaml instead of one shared project

The root `azure.yaml` isn't a scaffold waiting to be filled in — it already
targets a *live* container app in `rg-thinkschool-dev2` with a real
connection string in the file itself, and "no loss in any task" means Day 24
cannot be the day that project's `azd up` starts also trying to reconcile a
completely different resource group it never provisioned. Separately, azd's
`deploymentStacks` config lives at the project level, not per-environment —
there's no documented way to say "use `main.dev.bicepparam` for dev and
`main.prod.bicepparam` for prod" inside one `azure.yaml`. A second,
folder-scoped project sidesteps both: it can't touch the live app because
azd never looks at the root file when run from `infra/`, and the
environment-to-parameter-file mapping is one script instead of a second
`azure.yaml`.

## Why not just a hook

The obvious design is a `preprovision` hook that writes `main.bicepparam`
before azd deploys. That was the first thing built here, and it doesn't
work: azd resolves infrastructure parameters *before* running
`preprovision`, so with no parameters file on disk yet, azd falls back to
prompting interactively for every parameter it can't infer from a
SCREAMING_SNAKE_CASE environment variable — including `sqlDatabaseSku` and
`serviceBusSubscriptions`, an object and an array of objects that cannot be
answered at a prompt at all. Full transcript in Finding 1.

`scripts/azd-provision.ps1` is the fix: it calls `select-bicepparam.ps1`
directly, then calls `azd`, so the file exists before azd ever asks. The
hook stays in `azure.yaml` too, as a cheap re-assertion for anyone who calls
`azd provision` by hand instead of the wrapper — it just isn't load-bearing
the way it first looked.

## Commands

One-time, per machine:

```powershell
azd config set alpha.deployment.stacks on
```

From inside `infra/` (never the repo root):

```powershell
cd infra
azd env new dev --location centralindia
azd env set SQL_AAD_ADMIN_LOGIN     (az ad signed-in-user show --query userPrincipalName -o tsv)
azd env set SQL_AAD_ADMIN_OBJECT_ID (az ad signed-in-user show --query id -o tsv)

.\scripts\azd-provision.ps1 -Environment dev -ReuseManagedEnvironment
```

`centralindia`, not `southindia` — Finding 2. `-ReuseManagedEnvironment` —
Finding 7; without it this deployment fails partway through on this
subscription, every time, for a reason nothing in the template can fix.
Drop the switch anywhere with quota to spare and the stack creates its own
environment as before.

**Check the flag before believing the output.** `azd config get
alpha.deployment.stacks` must say `"on"`, and the prod plan below turns it
off. Left off, everything here still deploys and still reports `SUCCESS` —
as a plain deployment, with `azure.yaml`'s entire `deploymentStacks` block
silently discarded and no warning of any kind. Finding 10. Confirm with
`az stack sub list`, not with azd's exit message:

```powershell
azd config get alpha.deployment.stacks   # expect "on"
az stack sub list -o table               # expect azd-stack-dev
```

Teardown is the same command either way, and it does not take the borrowed
environment with it — the stack never managed it:

```powershell
azd down --force --purge
```

There is no `-Preview` that actually plans a deployment stack — see Finding
3. `.\scripts\azd-provision.ps1 -Environment dev -Preview` exists in the
script and will run, but it hits the same "preview not supported" error
Finding 3 documents; it isn't a working dry run today, and the script
doesn't pretend otherwise.

```powershell
azd env new prod --location <a region Microsoft.App supports - see Finding 4>
azd env set SQL_AAD_ADMIN_LOGIN     <a group, not a person>
azd env set SQL_AAD_ADMIN_OBJECT_ID <that group's object ID>
azd env set API_CONTAINER_IMAGE     <any real image reference>

azd config set alpha.deployment.stacks off   # --preview needs this off - Finding 3
.\scripts\azd-provision.ps1 -Environment prod -Preview
azd config set alpha.deployment.stacks on    # back on before touching dev again
```

**Always through `azd-provision.ps1`, never `azd provision` directly - even
for a plan.** Finding 5 is what happens otherwise: a stale `main.bicepparam`
left over from a previous environment gets used with no warning at all,
because the `preprovision` hook that would refresh it runs one step too
late to matter. The wrapper selects the file first every time; a bare
`azd provision --preview` does not, regardless of which environment is
currently selected.

## How far dev got, and what it took to get the rest of the way

Six problems surfaced running this against the actual subscription, each
below with its transcript. A seventh change fixed the worst of them;
findings 8 and 9 are the two bugs that fix introduced, one caught by review
and one by running it. Findings 10 to 13 are four more azd
behaviours, three of them occasions where azd's report and Azure's actual
state disagreed — which turned out to be the theme of the whole day. Two of them were regional or quota limits that
no static check can see; three were azd behaviours that are wrong in ways
that look right; one was a stale file that planned the wrong environment
and reported success.

The one that mattered most: the managed identity, Service Bus namespace and
SQL server all deployed successfully and were tracked as a genuine Azure
Deployment Stack, then the deployment hit
`MaxNumberOfGlobalEnvironmentsInSubExceeded`. This subscription allows
exactly one Container Apps managed environment and already has one — the one
the live `quotes-api` runs in. No region change fixes that; it's a
subscription-wide ceiling, not a regional one, and the two obvious ways past
it are a support-ticket quota increase or decommissioning the live app's
environment. This exercise does neither, and for a while that read as the
end of the road.

It wasn't, because the ceiling is on *creating an environment*, not on
running an app. A managed environment is shared infrastructure by design —
it exists precisely so several container apps can sit in it — so the
template asking for a private one was a choice, not a requirement. Finding
7 makes that choice a parameter, and with it the whole stack deploys: group,
identity, SQL server and database, Service Bus namespace with both
subscriptions and their filters, and the container app itself, all under one
Deployment Stack, with the environment as the single borrowed piece the
stack deliberately does not manage. The state at the top of this file is
that deployment, and `azd-stack-dev` reporting `succeeded` is what closes
the exercise.

Getting there needed two more corrections that had nothing to do with the
quota: the app had to be renamed before it collided with the live one
(Finding 8), and the wrapper had to stop being killed by azd's own update
banner (Finding 9).

### Finding 1 — a preprovision hook fires too late to supply parameters

```
? Enter a value for the 'apiContainerImage' infrastructure parameter: [Type ? for hint]
```

Covered in "Why not just a hook" above. `SQL_AAD_ADMIN_LOGIN` and
`SQL_AAD_ADMIN_OBJECT_ID` *were* picked up automatically here, via azd's
name-to-environment-variable convention — which is what made this a subtle
bug rather than a total one: two of four missing parameters resolved fine,
so the first sign of trouble was a prompt for a third.

### Finding 2 — what-if passed, the real deployment didn't: region capacity

```
ProvisioningDisabled: Subscriptions are restricted from provisioning in this
region. Please choose a different region.
```

Day 23's `az deployment sub what-if` ran clean against `southindia`. The
real deployment failed there — a per-subscription SQL capacity restriction
what-if has no way to check, because it validates the template, parameter
types and RBAC, never regional capacity. Worse, the deployment had already
created the managed identity and the Service Bus namespace before SQL
failed, and no stack was persisted for the failed run:

```
$ az resource list -g rg-quotes-dev -o table
Name                         ResourceGroup   Location    Type                                              Status
quotes-id-dev                rg-quotes-dev   southindia  Microsoft.ManagedIdentity/userAssignedIdentities  Succeeded
quotes-sb-dev-e6oljhc2krrhe  rg-quotes-dev   southindia  Microsoft.ServiceBus/namespaces                   Succeeded

$ az stack sub list
[]

$ azd down --force --purge
  (✓) Done: No Azure resources were found.
SUCCESS: Your application was removed from Azure in 5 seconds.

$ az resource list -g rg-quotes-dev -o table
Name                         ResourceGroup   Location    Type                                              Status
quotes-id-dev                rg-quotes-dev   southindia  Microsoft.ManagedIdentity/userAssignedIdentities  Succeeded
quotes-sb-dev-e6oljhc2krrhe  rg-quotes-dev   southindia  Microsoft.ServiceBus/namespaces                   Succeeded
```

`azd down` reported success in 5 seconds and deleted nothing. With no stack
to enumerate, it found nothing to act on and called that success — the
dangerous kind of false positive, since the message reads as "you're done"
and nothing prompts a second look. Real cleanup needed
`az group delete --name rg-quotes-dev --yes`. See Finding 4 for the direct
contrast once a stack actually existed.

**Fix:** `location` in `main.dev.bicepparam` now reads
`readEnvironmentVariable('AZURE_LOCATION', 'southindia')`, so the region
moves with `azd env set` instead of a file edit. Unset, it still resolves to
`southindia` exactly as Day 23 documented — that day's verification is
unaffected.

### Finding 3 — `azd provision --preview` is not implemented for deployment stacks

```
ERROR: deployment failed: error deploying infrastructure: preview not supported
```

The plain Bicep provider maps `--preview` onto ARM what-if; the stacks
provider (alpha as of azd 1.31.1) has no equivalent yet. Confirmed by
turning `alpha.deployment.stacks` off, which restores `--preview` — at the
cost of the plan no longer being stack-aware. There's no per-environment
fix; it's a gap in azd itself, and it's why prod's plan (Commands, above)
has to toggle the feature off first.

### Finding 4 — a subscription quota, and `azd down` proven both ways

```
Location: Central India
  (✓) Done: Resource group: rg-quotes-dev (4.05s)
  (✓) Done: Service Bus Namespace: quotes-sb-dev-e6oljhc2krrhe (22.223s)
  (✓) Done: Azure SQL Server: quotes-sql-dev-e6oljhc2krrhe (1m13.217s)
ERROR: The deployment template contains errors.
```

Inner error, from `az deployment operation sub list`:

```
"code": "MaxNumberOfGlobalEnvironmentsInSubExceeded",
"message": "The subscription '109b67f4-3ed5-413c-bcb0-62c54340b387' cannot
have more than 1 Container App Environments."
```

Confirmed subscription-wide rather than regional by checking `Microsoft.App`'s
supported-locations list — Central India is on it, the deployment still
failed. The one environment this subscription is allowed is the one the
live `quotes-api` app already runs on.

Worth connecting to Day 23 directly: `api` is one of the two modules that
day's `what-if` reported `NestedDeploymentShortCircuited` on and explicitly
skipped validating, reasoning that it "resolves itself once identity is
real." That's correct about the unresolved-reference problem it was
diagnosing, and it doesn't extend to a subscription quota — a plan can only
skip past a check it never runs. The exact module `what-if` couldn't
validate is the exact module whose real preflight failure no plan could
have shown.

Because three resources had actually deployed this time, a stack persisted:

```
$ az stack sub list -o table
Name           State    Last Modified
azd-stack-dev  failed   2026-09-08T05:57:53.362225+00:00

$ azd down --force --purge
  (✓) Done: Deleted subscription deployment stack azd-stack-dev
SUCCESS: Your application was removed from Azure in 8 minutes 44 seconds.

$ az resource list -g rg-quotes-dev -o table
(ResourceGroupNotFound) Resource group 'rg-quotes-dev' could not be found.
```

Same two commands as Finding 2's teardown, opposite outcome, and the only
variable is whether a stack existed at the time: 5 seconds and nothing
deleted, versus 8 minutes 44 seconds and a resource group, SQL server and
Service Bus namespace genuinely gone. That pair is the clearest evidence in
this exercise for what a Deployment Stack actually is — not a policy
statement, but the specific piece of state that makes `azd down` capable of
doing its job at all. It also draws the line honestly: that guarantee exists
only *after* a deployment succeeds far enough to register one. On the
failure path in Finding 2, there was nothing for it to protect yet.

### Finding 5 — running the plan outside the wrapper silently planned the wrong environment

The first prod attempt used `azd provision --preview` directly instead of
`azd-provision.ps1`, on the reasoning that stacks was already off so the
wrapper's broken `-Preview` path (Finding 3) didn't matter. It mattered for
a different reason: `main.bicepparam` still held *dev's* content, left over
from Finding 4's run, and a bare `azd provision --preview` only refreshes it
via the `preprovision` hook - which, per Finding 1, runs after azd has
already resolved parameters. There was no file missing this time, so there
was nothing to prompt for either. It just silently planned dev's resources
under the `prod` label:

```
Location: Central India
  Resources:
  Create : Resource group   : rg-quotes-dev
  Create : Azure SQL Server : quotes-sql-dev-e6oljhc2krrhe
SUCCESS: Generated provisioning preview in 30 seconds.
```

That's a worse failure mode than Finding 1, not a repeat of it. A missing
parameters file makes noise - an interactive prompt impossible to miss. A
*stale* one makes none: azd reports success, the resource names are
plausible at a glance, and the only tell is reading the resource group name
against which environment you meant to be looking at. Re-running through
the wrapper (`azd-provision.ps1 -Environment prod -Preview`) selects the
correct file first and produced the real plan below.

### Finding 6 — the real prod plan, and the same short-circuit Day 23 documented

```powershell
$env:SQL_AAD_ADMIN_LOGIN     = (az ad signed-in-user show --query userPrincipalName -o tsv)
$env:SQL_AAD_ADMIN_OBJECT_ID = (az ad signed-in-user show --query id -o tsv)
$env:API_CONTAINER_IMAGE     = "mcr.microsoft.com/k8se/quickstart:latest"
az deployment sub what-if --name quotes-prod-plan --location centralindia --template-file main.bicep --parameters main.bicepparam
```

(Raw `az deployment sub what-if` rather than `azd provision --preview` here,
for the detail azd's own summary doesn't show - and because a raw `az`
command doesn't see azd's `.env` at all, which is why the three
`$env:` lines above are needed first; azd injects those itself when it's
the one calling `az`.)

```
Scope: /subscriptions/109b67f4-3ed5-413c-bcb0-62c54340b387
  + resourceGroups/rg-quotes-prod [2024-03-01]
      location: "southindia"
      tags.environment: "prod"
      tags.dataClassification: "internal"

  + Microsoft.ManagedIdentity/userAssignedIdentities/quotes-id-prod
  + Microsoft.Sql/servers/quotes-sql-prod-zcebapajgws7q
      properties.administrators.azureADOnlyAuthentication: true
      properties.administrators.principalType: "Group"
      properties.administrators.login: "*******"
      properties.minimalTlsVersion: "1.2"
  + Microsoft.Sql/servers/.../databases/quotesdb
      sku.name: "GP_Gen5_2", properties.maxSizeBytes: 34359738368
  + Microsoft.Sql/servers/.../firewallRules/AllowAllWindowsAzureIps
      properties.startIpAddress / endIpAddress: "0.0.0.0"

Resource changes: 5 to create.

Diagnostics (2):
  (NestedDeploymentShortCircuited) ... reported against the `servicebus`
  and `api` module deployments.
```

Structurally identical to Day 23's dev what-if: 5 resources, the same two
`NestedDeploymentShortCircuited` diagnostics on `servicebus` and `api`, for
the same reason - both need `identity.outputs.principalId`, which doesn't
exist as a concrete value until identity has actually deployed. Confirms
the Entra-ID-only SQL admin (`principalType: "Group"`, no
`administratorLogin`/password anywhere), the General Purpose database at
the prod size, and the identical `0.0.0.0` firewall rule dev has.

One thing this run caught and fixed: the output above shows
`location: "southindia"` even though the deployment operation itself ran
against `centralindia` - `main.prod.bicepparam`'s `location` was still the
Day 23 hardcoded literal, unlike `main.dev.bicepparam`'s (Finding 2 fix).
Left alone, a real prod deployment would have carried Finding 2's exact,
already-diagnosed SQL regional restriction into prod the first time anyone
ran it. Now reads `readEnvironmentVariable('AZURE_LOCATION', 'southindia')`,
same pattern as dev, same default if unset.

### Finding 7 — the quota is on creating an environment, not on running an app

Findings 2 and 4 both end the same way: a real deployment discovers a limit
that every pre-deployment check passed straight through. Finding 2's was
regional and moved with a parameter. Finding 4's looked absolute:

```
"code": "MaxNumberOfGlobalEnvironmentsInSubExceeded",
"message": "The subscription '109b67f4-...' cannot have more than 1
Container App Environments."
```

Read carefully, that error is narrower than it first appears. It caps
*environments*, and says nothing about apps. A Container Apps managed
environment is a shared boundary by design — a VNet, a Log Analytics
destination and a Dapr/KEDA control plane that any number of container apps
can sit inside. `main.bicep` creating one per stack wasn't a requirement of
the workload; it was the default that made a self-contained template tidy,
and on a subscription with one environment slot it's the single line that
makes the template undeployable.

So the environment became optional:

```bicep
// modules/api.bicep
var createsManagedEnvironment = empty(existingManagedEnvironmentId)

resource logAnalytics '...workspaces@2023-09-01' = if (createsManagedEnvironment) { ... }
resource managedEnvironment '...managedEnvironments@2024-03-01' = if (createsManagedEnvironment) { ... }

var resolvedEnvironmentId = createsManagedEnvironment
  ? managedEnvironment.id
  : existingManagedEnvironmentId
```

Four things about that are decisions rather than mechanics:

**The workspace is conditional on the same flag, not on its own.** Log
destination is a property of the *environment*, not the app. A workspace
created alongside a borrowed environment would receive nothing at all — an
empty resource that looks like working observability, which is worse than
not having one.

**The borrowed environment is not declared `existing`.** Reading it would
need permissions on a resource group this stack doesn't manage, to look up
an ID that was already passed in. The real reason is narrower though:
`denySettings` and `actionOnUnmanage: delete` apply to what the stack
manages. An `existing` reference is still just a read, but the habit it
encourages isn't — and a stack with any claim on the live app's environment
is a strictly worse outcome than the quota was. `azd down` on this stack
now deletes a resource group, a SQL server, a Service Bus namespace and a
container app, and leaves the environment exactly where it found it.

**`managedEnvironment.id` on a conditional resource is safe; `.properties`
would not be.** It compiles to `resourceId(...)` — string arithmetic on the
name, no read of Azure state — so it's valid even on the branch where the
resource is never deployed, and the ternary discards it there anyway.
Confirmed in the generated ARM rather than assumed:

```
resolvedEnvironmentId = [if(variables('createsManagedEnvironment'),
                            resourceId('Microsoft.App/managedEnvironments', parameters('environmentName')),
                            parameters('existingManagedEnvironmentId'))]
```

The `logAnalytics!.properties.customerId` inside the environment block is
the opposite case and needs the `!` non-null assertion: a conditional
resource has type `workspace | null`, Bicep can't see that its only
consumer carries the identical condition, and `bicep build` says so
(BCP318/BCP422). The assertion is the correct fix here specifically because
the condition is shared. A `?? ''` fallback would also compile — and would
deploy an environment wired to no workspace.

**A container app must sit in its environment's region, and here that
region can't be the stack's.** The existing environment is in `southindia`;
`southindia` won't provision a new Azure SQL server for this subscription
(Finding 2). So `apiLocation` splits the app's region from the stack's: app
in `southindia` beside the environment it borrows, everything else in
`centralindia`. That's a real cross-region hop from app to database and a
real latency cost — named here, and in `main.bicep`'s own description of
the parameter, rather than left to be found on a p99 chart later.

`-ReuseManagedEnvironment` on the wrapper discovers the ID and the region
with `az containerapp env list` instead of having anyone paste a
subscription-scoped resource ID, and refuses rather than guessing if it ever
finds more than one. The `else` branch clears both azd variables, which is
Finding 5's lesson applied prospectively: a value left over from a previous
run is exactly as dangerous as a stale parameters file, and for the same
reason — it makes no noise.

Verified statically before running: `bicep build main.bicep`, `bicep lint`
and `bicep build-params` all clean with zero warnings on both parameter
files, and the generated ARM inspected on both branches to confirm the two
conditions match, `environmentId` resolves to the borrowed ID, and the
`logAnalyticsWorkspaceId` output degrades to `''` rather than to a
dangling reference.

### Finding 8 — the reuse fix nearly took the live app's hostname

Finding 7 was written, compiled clean, linted clean, and reviewed as
correct before this surfaced. `main.bicep` names the container app from a
default:

```bicep
param apiName string = '${namePrefix}-api'   // -> 'quotes-api'
```

The live app in the environment being borrowed is called `quotes-api`.

A container app's name has to be unique within its **managed environment**,
not within its resource group, and its default hostname is derived from that
name. So Finding 7's change would have deployed a second `quotes-api` into
the environment the real one already occupies — best case a hard failure
after SQL and Service Bus were up (Finding 4's shape all over again), worst
case contention for `quotes-api.<env-domain>`, which is the hostname the
Day 17 Static Web App proxies `/api/*` to. Deploying into a different
resource group does not separate them. Only the name does.

`main.dev.bicepparam` and `main.prod.bicepparam` now set `apiName`
explicitly — `quotes-api-dev` and `quotes-api-prod` — rather than
overriding it only on the borrowed path, because a name that changes
depending on how the stack was invoked is a name that recreates the app the
first time someone invokes it differently.

The reason to write this one up rather than just fix it: every check that
passed was checking the template against itself. `bicep build` verifies
types, `lint` verifies style, `what-if` verifies the plan against the
subscription's *current* state — and none of them know that a name which is
unique in `rg-quotes-dev` stops being unique the moment the app joins
somebody else's environment. The bug was created by the fix, in the gap the
fix opened, and it was found by re-reading the change against the live
system's actual resource names. That's the real lesson from Finding 7:
borrowing shared infrastructure means every assumption of the form "this
stack is alone in here" has to be re-checked, and naming is the one that
hides best.

Two smaller defects came out of the same re-read, both now fixed:

**`apiLocation` moved more than it claimed to.** `modules/api.bicep` uses
one `location` for the app, the environment and the workspace, so on the
path where the stack creates its own environment, a leftover `API_LOCATION`
would have relocated all three. It is now ignored unless
`existingManagedEnvironmentId` is set — the only case where the app's region
is legitimately not the stack's.

**The reuse switch could have pointed the stack at an environment inside
its own resource group.** `denySettings` and `actionOnUnmanage.resources`
genuinely cannot touch an unmanaged resource, which is Finding 7's whole
safety argument — but `actionOnUnmanage.resourceGroups: delete` is not
resource-scoped, and deleting a group takes unmanaged contents with it. The
one arrangement where `azd down` would destroy the environment it borrowed
is the one where that environment sits in the stack's own group, and being
unmanaged is exactly why nothing would stop it. The script now refuses that
case instead of relying on the live environment happening to live in
`rg-thinkschool-dev2`.

### Finding 9 — `2>$null` is not "ignore this" on Windows PowerShell

The first real run of `-ReuseManagedEnvironment` got all the way through
discovery and then died on the script's own error handling:

```
reusing 'cae-jlwf2oyjdsjjg' in South India [state: Succeeded]
  /subscriptions/109b67f4-.../resourceGroups/rg-thinkschool-dev2/providers/Microsoft.App/managedEnvironments/cae-jlwf2oyjdsjjg

azd : Update available: 1.31.1 -> 1.33.0
At C:\Users\dell\thinkschool\repo-live\infra\scripts\azd-provision.ps1:159 char:22
+     $stackLocation = azd env get-value AZURE_LOCATION 2>$null
+                      ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
    + FullyQualifiedErrorId : NativeCommandError
```

The thing that killed the run was an *advertisement*. `azd` writes "Update
available: 1.31.1 -> 1.33.0" to stderr on every invocation, and on Windows
PowerShell 5.1 redirecting a native command's stderr — `2>$null` very much
included — wraps that stderr in an `ErrorRecord`. Under
`$ErrorActionPreference = 'Stop'`, an `ErrorRecord` is terminating. So
`2>$null` did not suppress azd's chatter; it *promoted* it from console
noise to a fatal error, and the run's success depended on whether the azd
team had shipped a release recently.

Two things make this worth writing down rather than just fixing.

**The guard was the bug.** Every one of the three `2>$null` in this script
was added *for* robustness — the calls behind them ask "is this variable
set?", where a non-zero exit and a complaint on stderr are the expected
answer, not a failure. Leaving stderr alone would have worked fine. The
defensive redirection is the only reason the script could crash there at
all.

**The fix I'd already written was aimed at the wrong shell.** The script
sets `$PSNativeCommandUseErrorActionPreference = $false` at the top,
precisely to stop native exit codes becoming terminating errors — but that
variable only exists in PowerShell 7, and this ran on 5.1, where the
mechanism isn't exit codes at all, it's stderr redirection. A mitigation
for the right class of problem in the wrong shell is indistinguishable from
no mitigation, and nothing in the code said which shell it assumed.

Both native-capture sites now go through one helper that relaxes
`$ErrorActionPreference` for the duration of the call, so stderr stays
suppressed *and* suppressing it is not fatal, with `$LASTEXITCODE` still
checked by hand afterwards:

```powershell
function Invoke-NativeCapture {
    param([Parameter(Mandatory)][scriptblock]$Command)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try   { $output = & $Command 2>$null }
    finally { $ErrorActionPreference = $previous }
    return @($output | Where-Object { "$_".Trim() -ne '' })
}
```

`azd provision` itself is deliberately *not* routed through it — its output
is meant to stream to the console, and capturing it would trade a live
deployment log for a silent wait.

No Azure state was harmed: the crash happened after both azd variables were
written and before `azd provision` was reached, so the environment was left
correctly configured and re-running picked up where it stopped.

### Finding 10 — azd read the whole `deploymentStacks` block and silently ignored it

The first successful full deployment was not a Deployment Stack at all, and
nothing in azd's output said so:

```
  (✓) Done: Resource group: rg-quotes-dev (2.601s)
  (✓) Done: Service Bus Namespace: quotes-sb-dev-e6oljhc2krrhe (19.738s)
  (✓) Done: Azure SQL Server: quotes-sql-dev-e6oljhc2krrhe (1m14.901s)
SUCCESS: Your application was provisioned in Azure in 3 minutes 18 seconds.
```

```
$ az stack sub list -o table
$ azd config get alpha.deployment.stacks
"off"
```

`alpha.deployment.stacks` was still `off` from the prod `--preview` work,
where Finding 3 requires turning it off. `infra/azure.yaml` hands azd a
fully populated `deploymentStacks` block — `actionOnUnmanage`,
`denySettings`, an `excludedActions` list. azd parsed that file, discarded
the block entirely, deployed as a plain `Microsoft.Resources/deployments`,
and reported `SUCCESS` without one word about the configuration it had just
thrown away.

The only tells were both outside the output a person reads: an empty
`az stack sub list` afterwards, and the deployment name in a portal URL —
`.../deployments/dev-1788872289` on the plain run versus
`.../deployments/azd-stack-dev-26090813299ze` once the flag was on.

Two reasons this is worse than Finding 5, which it otherwise resembles.
Finding 5 at least left a wrong resource-group name visible in the plan.
Here there is no tell in the output at all. And azd was not being quiet in
general on that run — it warned about a reserved word in a firewall rule
name that turned out to be harmless (Finding 11), while saying nothing about
silently disabling the entire mechanism the deployment was meant to use. A
tool that warns about the cosmetic and stays silent on the structural trains
you to read its warnings as noise.

It also handed this exercise a control group nobody planned. The same
template, the same parameters, the same subscription, deployed twice within
the hour — once as a plain deployment and once as a stack — which is what
makes the comparison below evidence rather than a claim.

### Finding 11 — a linter that predicted certain failure, twice, wrongly

```
(!) Warning: Resource "quotes-sql-dev-e6oljhc2krrhe/AllowAllWindowsAzureIps"
    (Microsoft.Sql/servers/firewallRules) contains the reserved word "WINDOWS"
    Azure does not allow reserved words in resource names.
    The deployment will fail.
```

It did not fail. The warning fired on both the plain run and the stack run,
and both succeeded.

The rule azd is applying is real, but it governs resources with globally
addressable DNS names. A SQL firewall rule is a child resource with no
hostname of any kind, so there is nothing for a reserved word to collide
with. Worse for azd's case, `AllowAllWindowsAzureIps` is not a name anyone
invented here: it is the exact name the Azure portal itself generates for
that rule, which is why Day 23 chose it — renaming it would have made the
template describe something the portal does not produce.

What makes this worth recording is the confidence. Not "this may fail" or
"check this name" but *"The deployment will fail."* A flat, checkable
prediction, wrong twice in a row. Acting on it would have meant renaming a
correct resource to fix a problem that does not exist — and the prompt it
gates (`Proceed with provisioning despite the warnings above?`) is
engineered to make proceeding feel like the reckless choice.

Set against Finding 4, the pair is almost too neat: there, every static
check passed clean and the real deployment failed on a subscription quota;
here a static check declared certain failure and the deployment succeeded.
In both cases the check was not examining the thing it claimed to rule on.

### Finding 12 — "deployment failed" when the deployment had succeeded

A later stack run ended like this:

```
  (✓) Done: Resource group: rg-quotes-dev (4.809s)
  (✓) Done: Service Bus Namespace: quotes-sb-dev-e6oljhc2krrhe (1.808s)
  (✓) Done: Azure SQL Server: quotes-sql-dev-e6oljhc2krrhe (9.421s)
ERROR: deployment failed: error deploying infrastructure: deploying to
subscription: Get "https://management.azure.com/subscriptions/109b67f4-.../
deploymentStackOperationStatus/e13f50f6-...": dial tcp: lookup
management.azure.com: no such host
```

Then, minutes later, with no further commands run against Azure:

```
$ az stack sub list -o table
Name           State      Last Modified
-------------  ---------  --------------------------------
azd-stack-dev  succeeded  2026-09-08T13:34:47.990063+00:00
```

The timestamp is *after* the error. The DNS lookup failed inside azd's
polling loop; a deployment stack operation is asynchronous, so Azure carried
on and finished it while azd could no longer see it. Nothing was wrong with
the template, the parameters or the stack.

The error message describes the deployment. What failed was azd's
*knowledge* of the deployment, and the two are not the same thing. The tell
is inside the error itself: the failing call is a `Get` on
`deploymentStackOperationStatus` — a read. A failed read cannot fail a
write. But the sentence in front of it says `deployment failed`, so the
natural next move is to re-run a deployment that already worked, or to start
debugging a template that was never at fault.

Together with Findings 5 and 10, that is three occasions in one session
where azd's report and Azure's state disagreed, plus Finding 11 predicting a
failure that never happened. The common shape: azd reports on its own view,
and that view can be stale, partial, or about something other than what the
message claims. The habit worth keeping is to confirm against the resource
provider — `az stack sub list`, `az resource list` — rather than against the
tool that just told you what it thinks it did.

### Finding 13 — `shell: pwsh` names a shell this machine does not have

```
WARNING: PowerShell 7 (`pwsh`) commands found in project. Your computer only
has PowerShell 5.1 (`powershell`) installed. azd will use `powershell` but
errors may occur.
```

`infra/azure.yaml` declares `shell: pwsh` for the `preprovision` hook —
azd's hook shells are `pwsh` or `sh`, so there is no way to *declare* 5.1 —
and this machine has only 5.1. azd substituted it and carried on.

"Errors may occur" is doing a great deal of work in that sentence, and
Finding 9 is what it looks like when they do: 5.1 is precisely the shell
whose stderr-redirection behaviour turned an azd update banner into a fatal
error in `azd-provision.ps1`. The same substitution applies to the
`preprovision` hook, which survives only because `select-bicepparam.ps1`
does one `Copy-Item` and touches no native command's stderr.

So the warning and Finding 9 are one root cause seen from two directions: a
project that says `pwsh`, a machine that has 5.1, and no error until a script
happens to depend on a behaviour that differs between them. Installing
`pwsh` would make the declaration true and is the real fix; until then, the
wrapper is written to be correct on both, which is why Finding 9's helper
relaxes `$ErrorActionPreference` rather than relying on the PowerShell 7-only
`$PSNativeCommandUseErrorActionPreference`.

## What Deployment Stacks add over Day 23's plain deployments

**One line, since the exercise asks for one:** a plain deployment has no
memory of what it created, so nothing can be reliably deleted, and nothing
can be protected; a Deployment Stack is that memory, which is why `azd down`
either lies about success or genuinely tears the environment down depending
on whether one exists.

That is not a paraphrase of the documentation. Finding 10 accidentally
produced the control group: the same template, same parameters, same
subscription, deployed twice inside an hour — once with
`alpha.deployment.stacks` off and once on.

| | plain deployment | deployment stack |
|---|---|---|
| deployment name | `dev-1788872289` | `azd-stack-dev-26090813299ze` |
| `az stack sub list` | *(empty)* | `azd-stack-dev  succeeded` |
| resources created | all six | all six, adopted, not rebuilt |
| record of what it manages | none | the stack's `resources` list |
| `denySettings` from `azure.yaml` | silently discarded | applied as deny assignments |
| `azd down` | 5 seconds, deletes nothing, reports success (Finding 2) | deletes the group, SQL server and namespace (Finding 4) |

The last row is the one that matters, and it is the only row where the
difference is visible to someone who is not looking for it. Both halves are
recorded earlier in this file, from two runs a day apart with the same two
commands and opposite outcomes: `azd down --force --purge` against no stack
finished in 5 seconds having deleted nothing at all, and reported
`SUCCESS: Your application was removed from Azure` — the dangerous kind of
false positive, because the message reads as "you're done" and nothing
prompts a second look. The same command against a real stack took 8 minutes
44 seconds and left `ResourceGroupNotFound`.

The second-order point is the interesting one though. A stack is the only
thing in this exercise that knows the difference between *this template's
resources* and *everything else in the subscription* — which is exactly why
Finding 7's borrowed environment is passed in as an ID and never declared
`existing`. Once teardown is one command that genuinely deletes things, what
the stack does **not** manage stops being a technicality and becomes the
safety boundary.

A plain `az deployment sub create` — or `azd provision` without
`alpha.deployment.stacks` — only ever adds and updates; it has no memory of
what it created last time, so a resource removed from `main.bicep` is
silently orphaned rather than deleted, and nothing stops someone editing a
stack-managed resource by hand in the portal between runs. Finding 4 is that
memory made concrete: the same `azd down`, run once against no stack and
once against a real one, either lies about success or genuinely tears down
a resource group, a SQL server and a Service Bus namespace in one command.
`denySettings.mode: denyDelete` is the other half — turning "nobody should
delete this outside the template" from a comment in a README into an actual
Azure RBAC deny assignment on every resource the stack manages (with the
caveat in "What would break this," below: it doesn't stop a write, only a
delete).

A Deployment Stack's `actionOnUnmanage: delete` and its per-resource deny
assignments apply to what it manages and nothing else. That is exactly what
you want pointed at this stack's SQL server, and exactly what you do not
want pointed at the live app's environment.

## What the stack actually manages, and what it deliberately doesn't

The safety argument in Finding 7 — that a borrowed environment must be
passed as an ID and never declared `existing`, because `denySettings` and
`actionOnUnmanage` apply to what a stack *manages* — was reasoning until
this. The stack's own inventory settles it:

```
$ az stack sub show --name azd-stack-dev --query "{denyMode:denySettings.mode, onUnmanage:actionOnUnmanage, resourceCount:length(resources)}" -o json
{
  "denyMode": "denyDelete",
  "onUnmanage": {
    "managementGroups": "detach",
    "resourceGroups": "delete",
    "resources": "delete",
    "resourcesWithoutDeleteSupport": "fail"
  },
  "resourceCount": 13
}
```

```
$ az stack sub show --name azd-stack-dev --query "resources[].id" -o tsv
.../resourceGroups/rg-quotes-dev
.../rg-quotes-dev/providers/Microsoft.App/containerApps/quotes-api-dev
.../rg-quotes-dev/providers/Microsoft.ManagedIdentity/userAssignedIdentities/quotes-id-dev
.../rg-quotes-dev/providers/Microsoft.ServiceBus/namespaces/quotes-sb-dev-e6oljhc2krrhe
.../namespaces/quotes-sb-dev-e6oljhc2krrhe/providers/Microsoft.Authorization/roleAssignments/30112872-...
.../namespaces/quotes-sb-dev-e6oljhc2krrhe/providers/Microsoft.Authorization/roleAssignments/6933fce2-...
.../namespaces/quotes-sb-dev-e6oljhc2krrhe/topics/quote-events
.../topics/quote-events/subscriptions/audit-log
.../topics/quote-events/subscriptions/search-indexer
.../topics/quote-events/subscriptions/search-indexer/rules/$Default
.../rg-quotes-dev/providers/Microsoft.Sql/servers/quotes-sql-dev-e6oljhc2krrhe
.../servers/quotes-sql-dev-e6oljhc2krrhe/databases/quotesdb
.../servers/quotes-sql-dev-e6oljhc2krrhe/firewallRules/AllowAllWindowsAzureIps
```

**The borrowed environment is not in the list, and neither is anything in
`rg-thinkschool-dev2`.** Every one of the 13 sits inside `rg-quotes-dev`.
With `resources: delete` *and* `resourceGroups: delete` set, everything
listed is something `azd down` will destroy — so the list is exactly the
blast radius, and the live app's managed environment is outside it. That is
the property the whole design of Finding 7 rests on, now checkable rather
than argued.

Three more things fall out of the same output.

**`firewallRules/AllowAllWindowsAzureIps` is in there, deployed and
managed** — the precise resource azd twice announced would fail to deploy
(Finding 11). Not merely "the deployment succeeded anyway": the allegedly
impossible resource is a tracked member of the stack.

**`search-indexer/rules/$Default` is managed and `audit-log` has no rule
entry at all.** That asymmetry is Day 23's `servicebus.bicep` comment
demonstrating itself — an explicit `sqlFilter` creates a rule resource that
replaces the default `TrueFilter`, while an empty `sqlFilter` leaves the
implicit `$Default` in place and never declares it, so the stack has nothing
to manage there. The template said this in a comment; the stack's inventory
is independent evidence that the comment was accurate and not just
plausible.

**`master` is absent.** It shows up in `az resource list -g rg-quotes-dev`
but is not stack-managed, because Azure creates it alongside any SQL server
and `main.bicep` never mentions it. The distinction a stack draws is not
"what is in this resource group" but "what did this template create" — which
is precisely why the borrowed environment stays out, and why `azd down`
against a plain deployment (Finding 2) had no way to know either.

`resourcesWithoutDeleteSupport: "fail"` is worth naming too. If the stack
ever manages something Azure cannot delete through the stack API, teardown
halts rather than quietly leaving it behind — the exact opposite of Finding
2's `azd down`, which reported success having deleted nothing at all.

## GitHub link

https://github.com/thinkbridge-thinkschool/Thinkbridge_Shruti_Sahrawat/tree/main/Days/day-24

The template and wrapper this write-up describes:
[`infra/main.bicep`](../../infra/main.bicep),
[`infra/modules/api.bicep`](../../infra/modules/api.bicep),
[`infra/azure.yaml`](../../infra/azure.yaml),
[`infra/scripts/azd-provision.ps1`](../../infra/scripts/azd-provision.ps1),
[`infra/README.md`](../../infra/README.md).

## What did you learn this session?

That "the subscription won't let me" is a claim worth re-reading before
accepting. `MaxNumberOfGlobalEnvironmentsInSubExceeded` stopped this
deployment three resources in, and I wrote most of a page arguing that was
a complete result — the quota is real, it's subscription-wide, and neither
of the two ways past it was mine to take. What I'd missed is that the limit
caps *environments*, not apps, and my template only needed its own
environment because creating one was the tidier default. Making that a
parameter took about twenty lines and turned a blocked deployment into a
finished one.

Then the fix introduced a worse bug than the one it solved — a second
`quotes-api` aimed at the live app's own hostname (Finding 8) — and
everything that passed cleanly passed because it was checking the template
against itself. `bicep build` checks types, `lint` checks style, `what-if`
checks the plan against current state; none of them know that a name unique
in one resource group stops being unique the moment the app joins someone
else's environment. Sharing infrastructure invalidates every assumption of
the form "this stack is alone in here," and naming is the one that hides
best.

And the thing I'll actually carry: **confirm against the resource
provider, not against the tool.** Four times in one session azd's output and
Azure's real state disagreed — a plan that silently used the wrong
environment's parameters (5), a `SUCCESS` that quietly wasn't a Deployment
Stack at all (10), a linter twice predicting certain failure that never came
(11), and an `ERROR: deployment failed` for a deployment that had already
succeeded while a DNS lookup broke in the polling loop (12). None of those
are bugs I could have avoided. What I can change is what I treat as
evidence: `az stack sub list` and `az resource list` describe Azure, and
`SUCCESS` describes what a tool believes about itself.

The wider version, which most of the thirteen point at: a plan is only
evidence about the things it actually checks. `what-if` validated the
template, the parameter types and the RBAC, then reported
`NestedDeploymentShortCircuited` on the two modules it couldn't reach — and
both real failures landed in exactly those two. Day 23 read that diagnostic
as "resolves itself once identity is real," which is true about the
unresolved reference and says nothing about a region with no capacity or a
subscription with no room. A skipped check is not a passed one, and it's
usually the interesting one.

## What would break this?

**A borrowed environment is someone else's to delete.** Finding 7's design
keeps this stack from having any claim on the live app's managed environment
— verified: it is not among the stack's 13 managed resources. Which
necessarily means nothing stops the reverse. If that
environment is deleted, or its region retired, or the live app torn down
along with it, this stack's container app goes with it and the Deployment
Stack has no record that it depended on anything: the environment was never
one of its managed resources, so `denyDelete` doesn't cover it and a
re-provision won't recreate it. The honest read is that the quota was
traded for a dependency, not removed. On a subscription with room, dropping
`-ReuseManagedEnvironment` is strictly better and is still the default.

**`-ReuseManagedEnvironment` is a one-way door, and the script can only warn
about it.** A container app's `location` and `environmentId` are both
immutable. Deploy dev once with the switch and once without, and the second
run asks Azure to move the same app to a different region *and* a different
environment; ARM answers "already exists in location 'southindia'" rather
than migrating it. Recreating is the only route, and `denySettings:
denyDelete` blocks the delete half of that — so the actual recovery is
`azd down --force --purge` first. The script now warns when it sees a prior
borrowed run instead of walking into it, but a warning is all it can do:
nothing in the template can make an immutable property mutable.

**Joining someone else's environment needs permission on it.** Passing an
ID rather than declaring the environment `existing` avoids needing *read*
access to that resource group, but creating a container app in an
environment still requires `Microsoft.App/managedEnvironments/join/action`
on it. A deployer scoped as Contributor on `rg-quotes-dev` alone gets
`AuthorizationFailed` on the last resource in the stack. It works here
because this runs as subscription Owner, which is worth stating rather than
implying the ID-passing sidesteps authorization as well as reads.

**`denyDelete` doesn't stop a manual edit.** It blocks deletion of a
stack-managed resource outside the stack's own deployment, but a portal edit
to, say, the SQL server's `minimalTlsVersion` still goes through. Catching
that needs `denyWriteAndDelete`, which this project doesn't use because it
would also block the legitimate image-update redeploy in
`infra/README.md`'s "Deploy" section — that redeploy goes through the
stack's own mechanism, so it would actually still be allowed either way, but
`denyDelete` was the more conservative setting to verify first, not the only
one available.

**The generated `main.bicepparam` has no protection against being
hand-edited.** If someone edits it directly instead of one of the two real
files, the next run silently overwrites their change with whichever
`main.<env>.bicepparam` matches. Correct given the file is documented as
generated and gitignored — but a real way to lose a change made in the
wrong file.

**A subscription-wide resource quota is invisible to every check that runs
before a real deployment.** `bicep build`, `bicep lint`, `bicep
build-params`, and `az deployment sub what-if` all passed clean against the
`api` module. `MaxNumberOfGlobalEnvironmentsInSubExceeded` only ever
surfaces at actual deployment, and specifically at the one module `what-if`
couldn't reach in the first place (Finding 4). No amount of static
validation catches this class of problem; the only way to know a
subscription has room is to actually try.
