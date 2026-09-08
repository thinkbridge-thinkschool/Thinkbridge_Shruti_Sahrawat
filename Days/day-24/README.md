# Day 24 — Deployment Stacks + azd

Deploys the Day 23 stack (`infra/main.bicep`, unchanged) through `azd`
instead of raw `az deployment sub` commands, wrapped in an Azure Deployment
Stack. Dev was run for real against the actual subscription and got as far
as a genuine, subscription-wide capacity ceiling — identity, Service Bus and
SQL all deployed and were tracked as a real stack before that ceiling hit;
see "How far dev actually got" below for why that's a complete result, not
a stopped-short one. Prod is planned, never deployed — same reasoning
`main.prod.bicepparam` already gives for itself, now applied to the whole
exercise.

## What's new

| File | What it is |
|---|---|
| [`infra/azure.yaml`](../../infra/azure.yaml) | A **second, separate** azd project living inside `infra/` — deliberately not the repo-root `azure.yaml`, which drives the real, live app in `rg-thinkschool-dev2`. `azd` resolves to whichever `azure.yaml` is nearest to the current directory, so everything below only ever runs from inside `infra/`. |
| [`infra/scripts/azd-provision.ps1`](../../infra/scripts/azd-provision.ps1) | The entry point. Selects the right `.bicepparam`, then calls `azd`. See "Why not just a hook" below — this exists because the more obvious design (a preprovision hook alone) doesn't work. |
| [`infra/scripts/select-bicepparam.ps1`](../../infra/scripts/select-bicepparam.ps1) | Copies `main.dev.bicepparam` or `main.prod.bicepparam` onto `main.bicepparam` — the one filename azd's Bicep provider actually reads. Called directly by `azd-provision.ps1`, and also wired as azd's `preprovision` hook as a re-assertion (see below for why it can't be the only mechanism). |
| `infra/main.bicepparam` (generated, gitignored) | Never hand-edited. Whichever of the two real files was last selected. |

`main.bicep`, `types.bicep` and every module are byte-for-byte unchanged
from Day 23. One line changed in `main.dev.bicepparam` — `location` — for a
real reason found while running this (see Finding 2).

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
azd env new dev --location southindia
azd env set SQL_AAD_ADMIN_LOGIN     (az ad signed-in-user show --query userPrincipalName -o tsv)
azd env set SQL_AAD_ADMIN_OBJECT_ID (az ad signed-in-user show --query id -o tsv)

.\scripts\azd-provision.ps1 -Environment dev
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

## How far dev actually got, and why that's a complete result

Four real problems surfaced running this against the actual subscription,
each below with its transcript. The short version: the managed identity,
Service Bus namespace and SQL server all deployed successfully and were
tracked as a genuine Azure Deployment Stack. The deployment then hit
`MaxNumberOfGlobalEnvironmentsInSubExceeded` — this subscription allows
exactly one Container Apps managed environment, and it already has one, the
one the live `quotes-api` app runs in. No region change fixes that; it's a
subscription-wide ceiling, not a regional one. Getting past it needs either
a support-ticket quota increase or decommissioning the live environment,
and this exercise does neither.

That's a real, deliberate stopping point rather than a failure to complete
the exercise. Everything Day 24 is actually testing — azd driving a
subscription-scoped Bicep template, the parameter-file-per-environment
mechanism, a Deployment Stack forming, and `azd down` correctly tearing one
down once it exists — is proven by three resources deploying and being
cleanly removed as a stack. What's left untested is standing up a fourth
resource type this subscription has no room for regardless of anything in
this exercise.

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

## What Deployment Stacks add over Day 23's plain deployments

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

## GitHub link

https://github.com/thinkbridge-thinkschool/Thinkbridge_Shruti_Sahrawat/tree/main/Days/day-24

Commit `7cb1839`.

## What did you learn this session?

<!-- one line, in your own words -->

## What would break this?

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
