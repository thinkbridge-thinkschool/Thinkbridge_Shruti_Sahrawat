# Migrating the whole stack to a different Azure subscription

Written when the original subscription's credits expired (12 Sep 2026) and
everything had to be stood up again somewhere else.

  from: Azure subscription 1  109b67f4-3ed5-413c-bcb0-62c54340b387  (expired)
  to:   Azure for Students    a0a4d2da-6d94-4e5e-a07a-e26a136b8822  (Amity University tenant)

Azure for Students carries roughly $100 of credit and no payment method, so
nothing here can quietly overrun into a bill - but it can exhaust the credit.
Prod's Premium Service Bus alone is about Rs 75/hour, which is the whole grant
in under two days. Prod is therefore a deploy-verify-tear-down exercise here,
not something to leave running. It is a runbook, not a
narrative: work it top to bottom, and tick things off.

The point it proves, incidentally, is the one Days 23-27 were built around.
Everything below that is *infrastructure* is one `azd provision` away, because
it is all in `infra/main.bicep`. What takes the time is the handful of things
that were never in the template: the registry, the image, the Static Web App
and its backend link, App Insights, and the CI identity.

## What exists, and where it comes from

| Thing | Source | Re-created by |
|---|---|---|
| Resource group, SQL server + db, Service Bus + topic + subscriptions, Key Vault + secret, Log Analytics, Container App, managed identity, role assignments | `infra/main.bicep` | `azd provision` |
| VNet, private DNS zones, private endpoints (Day 27) | `infra/modules/network.bicep`, `private-endpoint.bicep` | same, via `enablePrivateEndpoints` |
| Container registry | **not in the template** - passed in as `ACR_LOGIN_SERVER` / `ACR_RESOURCE_ID` | `az acr create`, by hand |
| The API image itself | built from `QuotesApi/` | `dotnet publish /t:PublishContainer` |
| Static Web App (frontend host) | **not in the template** | `az staticwebapp create`, by hand |
| SWA -> container app backend link | **not in the template, not in any file** | `az staticwebapp backends link`, by hand |
| Application Insights | **not in the template** (the old one lived in rg-thinkschool-dev2) | `az monitor app-insights component create`, by hand |
| GitHub OIDC identity + federated credentials | `docs/promotion-flow.md` | by hand, in the new tenant |
| SQL user for the managed identity | `infra/scripts/create-sql-user.sql` | run by hand against the new db |

The right-hand column is the honest measure of how complete the
infrastructure-as-code is. Five rows say "by hand", and each one is a thing
that has to be remembered rather than run.

## Phase A - identity in the new tenant

```powershell
az logout
az login                      # sign in as the account that owns the new subscription
az account show --query "{sub:id, name:name, tenant:tenantId, user:user.name}" -o json
```

Record the subscription id and tenant id; everything below needs them.

```powershell
$SUB = "a0a4d2da-6d94-4e5e-a07a-e26a136b8822"   # Azure for Students, Amity University tenant
az account set --subscription $SUB

# Providers. A fresh subscription has almost none of these registered, and each
# one fails its first deployment rather than registering on demand - Day 27 hit
# exactly this with Microsoft.ContainerInstance.
foreach ($ns in 'Microsoft.App','Microsoft.ContainerRegistry','Microsoft.Sql','Microsoft.ServiceBus','Microsoft.KeyVault','Microsoft.Network','Microsoft.OperationalInsights','Microsoft.Insights','Microsoft.ContainerInstance','Microsoft.Web') {
    az provider register --namespace $ns
}
```

Registration takes a few minutes. Check with:

```powershell
foreach ($ns in 'Microsoft.App','Microsoft.ContainerRegistry','Microsoft.Sql','Microsoft.ServiceBus','Microsoft.KeyVault','Microsoft.Network','Microsoft.OperationalInsights','Microsoft.Insights','Microsoft.ContainerInstance','Microsoft.Web') {
    "{0,-40} {1}" -f $ns, (az provider show --namespace $ns --query registrationState -o tsv)
}
```

## Phase B - the registry and the image

The template needs an image that exists before it can create a container app,
so the registry comes first.

```powershell
$RG_SHARED = "rg-quotes-shared"
$LOC       = "centralindia"
$ACR       = "crquotes$((Get-Random -Maximum 99999))"   # must be globally unique, lowercase alphanumeric

az group create -n $RG_SHARED -l $LOC
az acr create -n $ACR -g $RG_SHARED --sku Basic --location $LOC
$ACR_ID     = az acr show -n $ACR --query id -o tsv
$ACR_SERVER = az acr show -n $ACR --query loginServer -o tsv
```

Build and push the API straight from source - no Dockerfile needed, the .NET
SDK publishes the container itself:

```powershell
cd C:\Users\dell\thinkschool\repo-live
az acr login -n $ACR
dotnet publish QuotesApi -c Release /t:PublishContainer `
  -p:ContainerRegistry=$ACR_SERVER `
  -p:ContainerRepository=quotes-api `
  -p:ContainerImageTag=0.2.0
```

Confirm it landed:

```powershell
az acr repository show-tags -n $ACR --repository quotes-api -o table
```

## Regions: what this subscription will and will not host

Found the hard way, and the reason `main.bicep` changed during this migration.

Azure for Students carries a region policy. `az policy assignment list` shows
"Allowed resource deployment regions" with exactly five:

```
centralindia, eastasia, koreacentral, indiasouthcentral, uaenorth
```

Anything outside that list fails with `RequestDisallowedByAzure` - which is
how South India was ruled out, even though Container Apps supports it.

Within the allowed five, Central India takes SQL, Key Vault, Service Bus, the
VNet and the private endpoints without complaint, and refuses Container Apps
environments outright:

```
MaxNumberOfEnvironmentsInSubExceeded: The subscription cannot create Container
App Environments in region 'Central India'. Please try another region.
```

With zero environments in existence. So it is not a count being exceeded; that
region simply has no capacity for this subscription type. Probing the rest,
UAE North, East Asia and Korea Central all created one successfully - so there
is no global cap either, just a regional one.

That leaves the data tier in Central India and the app in UAE North, and
**that combination was previously inexpressible**. `apiLocation` was honoured
only when borrowing an existing environment and ignored when the stack created
its own, so the only way to move the app was `location`, which moves the
database with it. `main.bicep` now honours `apiLocation` on both paths; on the
owned path it moves the app, its managed environment and its Log Analytics
workspace together, because a container app must live in its environment's
region. The data tier stays at `location`.

It is the same cross-region hop Days 23-24 documented, reached from the
opposite direction: there the environment was fixed and the database had to
move, here the database is fixed and the environment has to.

Set it with:

```powershell
azd env set API_LOCATION uaenorth
```

## Phase C - provision dev

The azd environments still point at the old subscription, so repoint them.
`EXISTING_CONTAINERAPP_ENV_ID` must be cleared: the old value names a managed
environment in a subscription this account cannot see, and left set it makes
the container app fail to deploy into something that does not exist. Cleared,
the template creates its own environment - which is Day 23's original
behaviour, and correct here because a fresh subscription has the quota the old
one did not.

```powershell
cd C:\Users\dell\thinkschool\repo-live\infra
azd env select dev

azd env set AZURE_SUBSCRIPTION_ID        $SUB
azd env set AZURE_LOCATION               centralindia
azd env set EXISTING_CONTAINERAPP_ENV_ID ""
azd env set API_LOCATION                 ""
azd env set ACR_LOGIN_SERVER             $ACR_SERVER
azd env set ACR_RESOURCE_ID              $ACR_ID
azd env set API_CONTAINER_IMAGE          "$ACR_SERVER/quotes-api:0.2.0"

# The Entra admin for the new SQL server is whoever is signed in now.
azd env set SQL_AAD_ADMIN_LOGIN     (az ad signed-in-user show --query userPrincipalName -o tsv)
azd env set SQL_AAD_ADMIN_OBJECT_ID (az ad signed-in-user show --query id -o tsv)

# A fresh signing key for the new environment - never reuse the old one, and
# never commit it. Create().GetBytes(), not Fill(): Fill() does not exist on
# Windows PowerShell 5.1 and silently leaves 48 zero bytes (Day 26).
$bytes = [byte[]]::new(48)
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
azd env set JWT_SIGNING_KEY ([Convert]::ToBase64String($bytes))
```

Then:

```powershell
./scripts/azd-provision.ps1 -Environment dev
```

Note: no `-ReuseManagedEnvironment`. That switch exists to borrow the one
environment the old subscription allowed; here the stack owns its own.

Verify the private endpoints came up:

```powershell
./scripts/verify-private-dns.ps1 -Environment dev
```

## Phase D - the database user

The managed identity cannot read the database until it exists as a user in it,
and that is T-SQL with no ARM equivalent - the one step of this stack that was
always going to be manual (see `infra/README.md`).

```powershell
$SQLFQDN = az stack sub show --name azd-stack-dev --query "outputs.sqlServerFqdn.value" -o tsv
$IDENTITY = az stack sub show --name azd-stack-dev --query "outputs.managedIdentityName.value" -o tsv
"server: $SQLFQDN   identity: $IDENTITY"
```

Edit `infra/scripts/create-sql-user.sql` so the user name matches `$IDENTITY`,
then run it against `quotesdb` with an Entra admin login - sqlcmd with `-G`,
or SSMS / Azure Data Studio with "Microsoft Entra MFA".

```powershell
sqlcmd -S $SQLFQDN -d quotesdb -G -i scripts\create-sql-user.sql
```

## Phase E - Application Insights (Day 26's tracing)

`Program.cs` reads `APPLICATIONINSIGHTS_CONNECTION_STRING`, but no template
ever set it - the old connection string came from a resource in
rg-thinkschool-dev2. Recreate it and hand it to the container app:

```powershell
$AI_CONN = az monitor app-insights component create `
  --app appi-quotes --location $LOC --resource-group $RG_SHARED `
  --application-type web --query connectionString -o tsv

az containerapp update -n quotes-api-dev -g rg-quotes-dev `
  --set-env-vars "APPLICATIONINSIGHTS_CONNECTION_STRING=$AI_CONN"
```

This is a gap worth closing properly later: an app that silently runs without
tracing looks identical to one that is tracing correctly, which is precisely
the Day 26 finding.

## Phase F - the frontend

```powershell
$SWA = "quotes-ui-swa"
az staticwebapp create -n $SWA -g $RG_SHARED -l eastasia --sku Standard
```

Standard, not Free: linking a container app backend needs Standard.

Link the API so `/api/*` reaches it - the Angular app calls relative `/api`
paths, so without this link the frontend loads and every request 404s:

```powershell
$APIID = az containerapp show -n quotes-api-dev -g rg-quotes-dev --query id -o tsv
az staticwebapp backends link -n $SWA -g $RG_SHARED --backend-resource-id $APIID --backend-region southindia
```

Then take the deployment token and put it in GitHub as
`AZURE_STATIC_WEB_APPS_API_TOKEN` (Settings -> Secrets and variables ->
Actions -> Secrets), replacing the old one:

```powershell
az staticwebapp secrets list -n $SWA -g $RG_SHARED --query "properties.apiKey" -o tsv
```

Pushing any change under `quotes-ui/**` to `main` then deploys it
(`.github/workflows/deploy-swa.yml`).

## Phase G - CI/CD against the new tenant

Follow `docs/promotion-flow.md`, which has the full reasoning; the values all
change, the shape does not.

1. Create a user-assigned managed identity in the new tenant, give it **Owner**
   on the new subscription (Contributor is not enough - the template creates
   role assignments).
2. Add federated credentials for `repo:thinkbridge-thinkschool/Thinkbridge_Shruti_Sahrawat:environment:dev`
   and `:environment:prod`. If the new tenant also enables immutable OIDC
   subject claims, the first run fails with `AADSTS700213` and the error text
   contains the exact subject string to use instead - read it from the error
   rather than guessing.
3. Update the GitHub secrets: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
   `AZURE_SUBSCRIPTION_ID`, `SQL_AAD_ADMIN_LOGIN`, `SQL_AAD_ADMIN_OBJECT_ID`,
   `JWT_SIGNING_KEY`.
4. Set the **environment** variables for both `dev` and `prod`:
   `API_CONTAINER_IMAGE`, `ACR_LOGIN_SERVER`, `ACR_RESOURCE_ID`.

That last step is not optional housekeeping. Day 27 found the dev environment
had no `API_CONTAINER_IMAGE` variable, so every pipeline run fell back to
`main.dev.bicepparam`'s placeholder default and quietly replaced the real API
with `mcr.microsoft.com/k8se/quickstart:latest`, which listens on :80 while
ingress probes :8080. The app was down for hours and every deployment reported
success.

## Phase H - prod

Same as dev, with prod's values:

```powershell
azd env select prod
# ... the same azd env set lines, plus:
azd env set API_CONTAINER_IMAGE "$ACR_SERVER/quotes-api:0.2.0"
./scripts/azd-provision.ps1 -Environment prod
./scripts/verify-private-dns.ps1 -Environment prod
```

Two warnings that are still true in any subscription:

- Prod's Service Bus is **Premium**, roughly ₹75/hour. Tear it down between
  demos: `./scripts/azd-down.ps1 -Environment prod`.
- Prod sets `apiSchemaBootstrap = 'Migrate'` and the repo has only SQLite
  migrations, so the container crash-loops on `PendingModelChangesWarning`.
  Prod's infrastructure deploys; prod's app does not run. That is a known,
  documented state (`main.prod.bicepparam`), not something this migration
  introduced.

## Phase I - decommission the old subscription

Only after the new one is verified working.

```powershell
az account set --subscription 109b67f4-3ed5-413c-bcb0-62c54340b387
cd C:\Users\dell\thinkschool\repo-live\infra
./scripts/azd-down.ps1 -Environment prod
./scripts/azd-down.ps1 -Environment dev
az group delete -n rg-thinkschool-dev2 --yes    # the Day 5/17 live app, ACR, managed environment, SWA
az group list -o table                          # expect nothing left
```

Then remove the payment method: portal.azure.com -> Cost Management + Billing
-> Billing profile -> Payment methods. Do this last: while the subscription is
disabled or card-less, the old registry and database are unreachable, and you
want them available until the new environment is proven.
