# dev -> prod, and why the environment is shared

Code lands on `dev`, gets deployed and tested there, then moves to `main` by
pull request. Merging to `main` provisions prod. That is the whole flow, and
`.github/workflows/deploy-infra.yml` is the thing that performs it.

## What is shared and what is not

This subscription allows exactly **one** Container Apps managed environment,
and the live app already holds it (Days/day-24, Finding 7). So dev and prod
share that managed environment - the network and logging boundary - and nothing
else:

| Shared | Separate per environment |
|---|---|
| the managed environment (forced by the quota) | the container app |
| the Log Analytics workspace behind it | the SQL server and database |
| | the Service Bus namespace |
| | the Key Vault |
| | the resource group, and the deployment stack itself |

Names carry the environment, so nothing collides:
`${namePrefix}-sql-${environmentName}-${resourceToken}`, where the token is a
hash of subscription, resource group and environment name.

Sharing the managed environment is a compromise, not a design, and it costs two
things worth stating plainly:

- **No network isolation between dev and prod.** Fine for a training repo,
  wrong for real money.
- **One Log Analytics workspace**, so dev and prod telemetry land in the same
  pile. Day 26's queries all group by `cloud_RoleName`, so separating them is
  one `where` clause - but it is a clause someone has to remember to write.

What is *not* shared is the part that matters: the database. Testing on dev
cannot touch prod data, which is the only reason a promotion step means
anything.

## The pipeline

| Trigger | What happens |
|---|---|
| push to `dev` touching `infra/**` | provisions dev, no approval |
| merge to `main` touching `infra/**` | provisions prod, **after** a required reviewer approves |
| manual run (Actions -> deploy-infra) | either environment, `preview` defaults to true so the default manual run creates nothing |

The path filter is deliberate. A documentation commit must not stand up a
Premium Service Bus namespace, and an automated deploy that fires on every push
is a standing risk to the live app whose managed environment this borrows
(Finding 8 is what that mistake looks like).

Application code is not deployed by this workflow - `ci.yml` tests it and
`deploy-swa.yml` ships the frontend. This one owns infrastructure only, which
is the same separation `infra/README.md` already describes.

## Cost

Prod runs Premium Service Bus at roughly rupees 75 per hour - about rupees
54,000 a month if left standing. The approval gate on the `prod` environment
exists for that reason as much as for safety. Tear it down when the
demonstration is finished:

```powershell
cd infra
azd env select prod
azd down --force --purge
```

`--purge` matters: without it the Key Vault is soft-deleted, its name stays
reserved, and the next prod provision collides with it (Day 25 hit exactly
that).

## One-time setup

Sign-in is federated - GitHub gets a short-lived token from Azure and there is
no password or client secret stored anywhere.

Because this tenant's operator is a guest account, use a **user-assigned
managed identity** rather than an app registration; guests are commonly blocked
from creating app registrations, and a managed identity is an ordinary Azure
resource instead.

```powershell
$repo = "thinkbridge-thinkschool/Thinkbridge_Shruti_Sahrawat"
$rg   = "rg-thinkschool-dev2"
$sub  = (az account show --query id -o tsv)

az identity create -g $rg -n id-github-oidc
$clientId    = az identity show -g $rg -n id-github-oidc --query clientId -o tsv
$principalId = az identity show -g $rg -n id-github-oidc --query principalId -o tsv

# One credential per branch, plus one per GitHub environment when prod is gated.
foreach ($ref in @('dev','main')) {
  az identity federated-credential create `
    --name "gh-$ref" --identity-name id-github-oidc -g $rg `
    --issuer "https://token.actions.githubusercontent.com" `
    --subject "repo:${repo}:ref:refs/heads/$ref" `
    --audiences "api://AzureADTokenExchange"
}
foreach ($envName in @('dev','prod')) {
  az identity federated-credential create `
    --name "gh-env-$envName" --identity-name id-github-oidc -g $rg `
    --issuer "https://token.actions.githubusercontent.com" `
    --subject "repo:${repo}:environment:$envName" `
    --audiences "api://AzureADTokenExchange"
}

# Owner, not Contributor. The template creates role assignments of its own -
# Key Vault Secrets User for the API's identity (Day 25), AcrPull for the
# registry - and Contributor cannot create role assignments.
az role assignment create --assignee $principalId --role Owner --scope "/subscriptions/$sub"
```

Then in GitHub, Settings -> Secrets and variables -> Actions:

| Secret | Value |
|---|---|
| `AZURE_CLIENT_ID` | the `clientId` printed above |
| `AZURE_TENANT_ID` | `az account show --query tenantId -o tsv` |
| `AZURE_SUBSCRIPTION_ID` | `az account show --query id -o tsv` |
| `JWT_SIGNING_KEY` | a fresh 48-byte base64 key, never a reused one |
| `SQL_AAD_ADMIN_LOGIN` | the SQL Entra administrator's UPN |
| `SQL_AAD_ADMIN_OBJECT_ID` | that principal's object ID |

| Variable | Value |
|---|---|
| `AZURE_LOCATION` | `centralindia` |
| `API_CONTAINER_IMAGE` | a real image reference; prod has no default and fails without it |
| `SQL_AAD_ADMIN_PRINCIPAL_TYPE` | `User` while the admin is a person rather than a group |
| `ACR_LOGIN_SERVER`, `ACR_RESOURCE_ID` | only if pulling from a private registry |

Finally, Settings -> Environments -> `prod` -> add yourself as a required
reviewer. Without that, a merge to `main` provisions prod unattended.

Generate the signing key like this - the `Fill()` form does not exist on
Windows PowerShell 5.1, and fails in a way that still prints success while
storing 48 zero bytes (Day 26, Finding 7):

```powershell
$bytes = [byte[]]::new(48)
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
[Convert]::ToBase64String($bytes)
```
