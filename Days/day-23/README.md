[← Back to full README](../../README.md)

## Day 23 — Bicep IaC

Describe the infrastructure as code: parameterized Bicep modules for the API,
SQL and Service Bus, with separate dev and prod parameter files, and no portal
click-ops.

Everything lives in [`infra/`](../../infra/). Its
[README](../../infra/README.md) is the runbook; this page is the reasoning.

### What was already true before this

This stack was not built from nothing. `QuotesApi` has been running as an Azure
Container App since Day 5, against an Azure SQL database, alongside a Standard
Service Bus namespace that Day 19 used for real. None of it was described
anywhere: [`azure.yaml`](../../azure.yaml) names a service, a host and three
environment variables, and `azd` inferred everything else — the resource group,
the registry, the managed environment, the workspace, the identity, the
firewall rule. That inference is genuinely convenient and it is also the exact
problem this exercise names. There is no file anywhere in this repo that says
what tier that database is, why the Service Bus namespace has to be Standard, or
that a firewall rule admitting every Azure tenant exists at all. Those facts
live in a portal blade and in one person's memory.

So the deliverable here is not "make it deployable" — it already deploys. It is
**make it legible**: the same stack, expressed as something a reviewer can read,
diff, and disagree with before it costs money.

### Structure, and why it is subscription-scoped

[`main.bicep`](../../infra/main.bicep) targets a *subscription*, not a resource
group, and creates the group itself. A group-scoped template can only deploy
into a group somebody already made, which leaves the first and most
consequential resource in the stack — its name, its region, its tags — as the
one thing still created by hand. It also means `what-if` can be run against an
environment that does not exist yet and still print a complete plan, which is
the property that makes reviewing a *new* environment possible at all.

Five modules, in dependency order:

| Module | Why it is its own file |
|---|---|
| [`identity.bicep`](../../infra/modules/identity.bicep) | Both data-plane modules need a principal to grant to, and the API needs a client ID for its connection string. Whoever creates it has to come first. |
| [`sql.bicep`](../../infra/modules/sql.bicep) | Server + database, Entra-ID-only. |
| [`servicebus.bicep`](../../infra/modules/servicebus.bicep) | Namespace, topic, subscriptions, filters, role assignments. |
| [`api.bicep`](../../infra/modules/api.bicep) | Container app, its managed environment, its workspace. |
| [`registry-access.bicep`](../../infra/modules/registry-access.bicep) | One role assignment, deployed at a *different* resource group's scope. |

The identity module is the one worth defending, because splitting it out looks
like over-decomposition until you try the alternative. Creating the identity
inside `api.bicep` would force `sql` and `servicebus` to depend on the API to
learn the principal they are granting access to — backwards, since the API is
the thing that depends on *them*, and a cycle the moment the API also needs the
SQL FQDN for its connection string. Pulling the identity out ahead of both
turns the graph back into a line.

It is user-assigned rather than system-assigned for a reason that only shows up
later: a system-assigned identity is created with its container app and
destroyed with it, so its object ID changes every time the app is recreated —
and every role assignment and every SQL user that referenced the old ID silently
stops matching anything, with no error until the first query fails at runtime.

`api.bicep` deliberately holds three resources rather than one. A container apps
environment cannot exist without a Log Analytics workspace to point at, and
neither outlives the app in any scenario this stack has. A module per resource
would be filing, not structure.

### There are no secrets in this stack, and that is a design decision

Nothing in `infra/` is marked `@secure()`, because nothing needs to be:

- **SQL** is created with `azureADOnlyAuthentication: true`. The server has no
  SQL login at all — not a strong one, not one in Key Vault, none. The absence
  of `administratorLogin`/`administratorLoginPassword` from the template is the
  control; with Entra-only enabled, ARM rejects them.
- **Service Bus** is created with `disableLocalAuth: true`, which removes SAS
  keys from the namespace entirely. Day 19's
  [`Quotes.Worker/appsettings.json`](../../Quotes.Worker/appsettings.json)
  already claims its empty `ConnectionString` is safe because "no key exists for
  this namespace to leak" — this is the line that makes that literally true
  rather than a convention someone can break by pasting a key into a config file.
- **The API** reaches both as a managed identity. The `User Id` in its
  connection string is the identity's *client* ID, which tells the driver which
  identity to request a token for when more than one is attached. It is an
  identifier, not a credential.
- **The Log Analytics shared key** the managed environment needs is read with
  `listKeys()` at deploy time rather than passed in, so it is never written
  down, never typed on a command line, and never lands in a deployment-history
  parameter record.

The one directory value that is not a secret but still does not belong in a
shared repo — the Entra admin's object ID — is read from the environment by both
parameter files via `readEnvironmentVariable`, so the files are identical
whoever runs them.

### dev and prod differ in ways that have reasons

Not bigger numbers for their own sake. The four that matter:

| | dev | prod | Why |
|---|---|---|---|
| SQL | `Basic`, 2 GB | `GP_Gen5_2`, 32 GB | Basic caps at 2 GB and keeps 7 days of backups. The jump is about restore window, not size. |
| Service Bus | `Standard` | `Premium` | Standard is the *floor*, not a choice: Basic has queues only, so Day 19's fan-out design is unavailable there. Premium adds dedicated capacity and zone redundancy. |
| API replicas | 0–2 | 2–10 | dev scales to zero and costs nothing when idle. |
| SQL admin | `User` | `Group` | A production server whose only administrator is one person's account is an outage waiting for a resignation. |

The replica floor is the one with a behavioural consequence rather than a cost
one, and it is written into
[`main.prod.bicepparam`](../../infra/main.prod.bicepparam) rather than left for
someone to rediscover: Day 21's HybridCache deduplicates a stampede *per
process*. With `minReplicas: 2`, a cold cache under load costs two factory
invocations, not one. Still a 200-to-2 reduction, but not the single-hit
guarantee the single-instance test proves — and pretending otherwise is how a
cache gets blamed for a database spike nobody can reproduce.

`main.prod.bicepparam` has never been deployed, and says so in its own header.
The exercise's budget is one environment, and standing up a Premium namespace to
prove a parameter file parses would be an expensive way to learn something
`bicep build-params` already answers. What it *is* verified to be is valid: it
compiles, and every value in it is type-checked against the template.

### The bug this exercise was always going to produce

The first draft of `servicebus.bicep` gave the search-indexer subscription its
filter as a new rule with a descriptive name — `search-indexer-quote-created`,
holding `eventType = 'QuoteCreated'`. It deploys cleanly. It is also wrong, and
wrong in the worst available way.

Creating a subscription creates a rule named `$Default` holding a **TrueFilter**
— that is what makes a brand-new subscription receive everything. Adding a
filter as a *second* rule does not replace it; Service Bus ORs the rules
together, so the TrueFilter keeps matching and the filter changes nothing at
all. Every `QuoteDeleted` still reaches the search indexer. The deployment
succeeds, `az servicebus ... rule list` shows the new rule sitting there looking
exactly right, and the only symptom is the indexer quietly doing work Day 19's
whole design says it must never see.

The fix is to overwrite `$Default` itself rather than add a sibling, which is
what the template now does — addressed by its full name, because a child
resource inside a loop cannot use `parent:` to point at one iteration of another
loop:

```bicep
resource defaultRule '...topics/subscriptions/rules@2022-10-01-preview' = [
  for (sub, i) in subscriptions: if (!empty(sub.sqlFilter)) {
    name: '${namespaceName}/${topicName}/${sub.name}/$Default'
    ...
```

This is documented Service Bus behaviour rather than something a compile step
can catch, so [`infra/README.md`](../../infra/README.md) carries the one command
that confirms it against a real namespace after a deploy — one rule, named
`$Default`, `SqlFilter`, right expression. A claim that can only be checked by
reading the template is not evidence.

The same file records the two other decisions that are easy to get backwards:
duplicate detection is **off** (it would swallow exactly the republished message
Day 20's outbox relay produces after a crash between publish and mark-sent, so
the `(MessageId, Consumer)` ledger built to absorb it would never be exercised —
a protection that silently stops being tested is worse than one never added),
and `deadLetteringOnFilterEvaluationExceptions` is **on** (a filter that throws
is a routing bug, not a message to discard).

### Making a bad parameter file fail at build instead of mid-deployment

`param subscriptions array` accepts anything: an entry missing
`maxDeliveryCount`, a lock duration of 600 seconds against a broker that caps at
300, a filter key spelled `filter` instead of `sqlFilter`. All of those compile.
All of them pass `what-if`. All of them fail partway through a deployment that
has already created half the stack, with an ARM error naming a property path
rather than the line in the parameter file that got it wrong.

[`types.bicep`](../../infra/types.bicep) declares the shape once, with
constraints, and both `main.bicep` and `servicebus.bicep` import it. Four
realistic mistakes, each injected into a copy of the dev parameter file, each
caught by `bicep build-params` before anything reached Azure:

```
### 1. subscription entry missing maxDeliveryCount
exit=1
BCP035: The specified "object" declaration is missing the following required properties: "maxDeliveryCount".

### 2. lock duration above the Service Bus 300s cap
exit=1
BCP327: The provided value (which will always be greater than or equal to 600) is too large
        to assign to a target for which the maximum allowable value is 300.

### 3. filter key misspelled (filter instead of sqlFilter)
exit=1
BCP035: The specified "object" declaration is missing the following required properties: "sqlFilter".
BCP037: The property "filter" is not allowed on objects of type
        "{ name: string, sqlFilter: string, maxDeliveryCount: int, lockDurationSeconds: int }".
        Permissible properties include "sqlFilter".

### 4. replica count given as a string
exit=1
BCP033: Expected a value of type "int" but the provided value is of type "'0'".

### control: unmodified file
exit=0
```

The control line is the point of the exercise: without it, four failures only
prove the build is capable of failing.

The same reasoning produced the `@minLength(36)` on `sqlAadAdminObjectId`.
Because Bicep evaluates `readEnvironmentVariable` at build time, an unset
`SQL_AAD_ADMIN_OBJECT_ID` now fails with `BCP333` at `bicep build-params` —
before a subscription is contacted — rather than producing a deployment with an
administrator that is the empty string.

[`bicepconfig.json`](../../infra/bicepconfig.json) raises most linter rules from
`warning` to `error`, so a hardcoded location, an unused parameter or a secret
in a default fails the build instead of scrolling past in a terminal.
`use-parent-property` is the one deliberate exception, left at `warning`, for
the `$Default` rule that cannot use `parent:` — an exception with a reason next
to it, rather than a rule quietly switched off.

### The step that is not Bicep, and is not hidden either

Bicep creates the SQL server, the database and the managed identity. It cannot
create the identity's **user inside the database** — that is
`CREATE USER ... FROM EXTERNAL PROVIDER`, T-SQL run against the database by a
connection holding an Entra admin token, and there is no ARM resource for a
database principal. An IaC repo that claims "no click-ops" and leaves this out
has moved the manual step somewhere less visible than the portal, not removed
it. [`infra/scripts/create-sql-user.sql`](../../infra/scripts/create-sql-user.sql)
is idempotent, parameterized by identity name, and wired into the runbook
immediately after the deploy command.

It also carries the one uncomfortable trade in this stack, in a comment rather
than in silence: `Database:SchemaBootstrap=Migrate` means EF applies migrations
on startup, which needs DDL rights, which is why the script grants `db_ddladmin`
alongside reader and writer. The better answer for anything with real data is to
run `dotnet ef database update` from CI with a deploy principal and leave the
app with reader/writer only. This stack does the simpler thing; the comment
exists so the next person can see it was a choice.

### CI

[`.github/workflows/infra.yml`](../../.github/workflows/infra.yml) — separate
from `ci.yml` and path-scoped to `infra/**`, the same way `deploy-swa.yml` is
scoped to `quotes-ui/**`. It builds, lints and type-checks both parameter files.
It holds no credentials and cannot deploy anything. What it prevents is the
failure mode that makes IaC worse than the portal: a template committed,
reviewed, merged, and then found to be invalid by the person who was counting on
it three weeks later.

### Verification status

Everything that can be checked without a subscription was checked in the build
environment, with Bicep CLI 0.46.1:

- `bicep build main.bicep` — exit 0, no warnings, under the strict
  `bicepconfig.json`. This compiles every module transitively.
- `bicep lint` on all five modules and `types.bicep` — exit 0 each.
- `bicep build-params` on both parameter files — exit 0, with the resolved
  values confirmed by reading them back out of the compiled JSON (dev:
  `Basic`/`Standard`/`minReplicas 0`; both: the two subscriptions with the
  filter expression intact).
- The four injected-mistake cases above, plus the passing control.

**`az deployment sub what-if` has now been run for real**, against
`rg-quotes-dev` in `southindia`, on a subscription this repo already uses.
Output, redacted only where noted:

```
Resource changes: 4 to create, 1 to modify.

  ~ resourceGroups/rg-quotes-dev [2024-03-01]
      + tags: costCentre, environment, managedBy, owner, workload

  + Microsoft.ManagedIdentity/userAssignedIdentities/quotes-id-dev
  + Microsoft.Sql/servers/quotes-sql-dev-e6oljhc2krrhe
      properties.administrators.azureADOnlyAuthentication: true
      properties.administrators.login: "*******"        <- CLI's own redaction
      properties.administrators.sid: "82378627-..."
      properties.minimalTlsVersion: "1.2"
  + Microsoft.Sql/servers/.../databases/quotesdb
      sku.name: "Basic", properties.maxSizeBytes: 2147483648
  + Microsoft.Sql/servers/.../firewallRules/AllowAllWindowsAzureIps
      properties.startIpAddress / endIpAddress: "0.0.0.0"

Diagnostics (2):
  (NestedDeploymentShortCircuited) A nested deployment got short-circuited
  and all its resources got skipped from validation. This is due to a nested
  template having a parameter that was not fully evaluated (e.g. contains a
  reference() function). — reported against the `servicebus` and `api`
  module deployments.
```

Confirms the four things worth confirming on a first plan: the identity, the
Entra-ID-only SQL server (`azureADOnlyAuthentication: true`, no
`administratorLogin`/password anywhere in the diff), the `Basic` database at
the dev size, and the `0.0.0.0` firewall rule — exactly, and only, the
resources the plan should show for a brand-new environment.

**The two diagnostics are a real, worth-knowing ARM limitation, not a broken
template.** `servicebus` and `api` each take `principalId`/`identityClientId`
as `identity.outputs.principalId`/`.clientId` — values that do not exist as
concrete strings until the `identity` module has actually deployed. `what-if`
evaluates the whole graph *without deploying anything*, so when a nested
module's input is a `reference()` to a sibling resource that is not yet real,
ARM cannot resolve it to a value and — rather than guess — skips validating
everything inside that module and says so explicitly, rather than silently
approving or silently failing. This is documented, expected behaviour for any
multi-module template wiring one module's output into another's input on a
*first* deployment into a brand-new environment (see the link ARM prints:
`aka.ms/WhatIfEvalStopped`), not something this template did wrong — and it
resolves itself the moment `identity` is real: rerunning `what-if` after the
first `deployment sub create` would fully evaluate `servicebus` and `api` too,
because `principalId` would then be a concrete GUID instead of an unresolved
reference. Worth stating plainly rather than either hiding the diagnostic or
overclaiming the plan proved more than it did.
