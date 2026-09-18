# Deployment — the current one

The live resources, and how to change them. Accurate as of 18 September 2026.

Two older documents in this repository — [DEPLOY-RUNBOOK.md](DEPLOY-RUNBOOK.md)
and [VERIFICATION-DEPLOY.md](VERIFICATION-DEPLOY.md) — describe a deployment in
a subscription that no longer exists. They are kept as the evidence for days 24
and 25 rather than corrected, because rewriting them to name today's resources
would misrepresent what was true then. This file is the one to trust.

## What is deployed

| | Name | Resource group | Notes |
|---|---|---|---|
| Subscription | Azure for Students | | `a0a4d2da-6d94-4e5e-a07a-e26a136b8822` |
| Web app | `quotes-ui-swa` | `rg-quotes-shared` | `https://black-sea-0f5ad2a00.5.azurestaticapps.net` |
| API | `quotes-api-dev` | `rg-quotes-dev` | Container App, UAE North |
| Database | `quotes-sql-dev-wewdp2fyybrgs` | `rg-quotes-dev` | Azure SQL, managed identity |
| Registry | `crquotes33928` | `rg-quotes-shared` | `crquotes33928.azurecr.io` |

## The entry point is the web app, not the API

The Container App is a **linked backend** of the Static Web App. Linking them
turns on Easy Auth on the Container App with the SWA itself as the identity
provider, so the Container App refuses anything that did not arrive through the
front door:

```
$ curl -i https://quotes-api-dev.redfield-acdee432.uaenorth.azurecontainerapps.io/health
HTTP/1.1 401 Unauthorized
www-authenticate: Bearer realm="quotes-api-dev.redfield-acdee432.uaenorth.azurecontainerapps.io"
x-ms-middleware-request-id: 1500c228-f04a-44c9-8694-c7675c9adca7
```

That 401 is the design, not a fault, and `x-ms-middleware-request-id` is how you
tell: it is the Easy Auth sidecar answering, not the application. `/health`
returns it too, so there is no public liveness URL on the API's own hostname.

Call the API through the web app instead:

```powershell
$ui = "https://black-sea-0f5ad2a00.5.azurestaticapps.net"
curl.exe -s -o NUL -w "%{http_code}`n" "$ui/api/quotes"   # 401 - the app's own JWT auth
```

## Deploying a new API image

There is no workflow for this; the image is built and pushed by hand. The
version lives in `QuotesApi.csproj` as `ContainerImageTag` and should be bumped
there rather than passed on the command line — production ran `0.2.0` for two
deploys while the csproj said `0.1.0`, which meant no commit could tell you what
image it produced.

```powershell
az acr login --name crquotes33928

dotnet publish QuotesApi\QuotesApi.csproj -c Release /t:PublishContainer `
  -p ContainerRegistry=crquotes33928.azurecr.io

az containerapp update -n quotes-api-dev -g rg-quotes-dev `
  --image crquotes33928.azurecr.io/quotes-api:<tag>
```

`--image` and nothing else, deliberately. It leaves the existing environment
variables and secrets alone. **Do not run `azd up`**: the root `azure.yaml` is
an azd manifest that is not the deployment path for these resources, and
applying it would overwrite `ConnectionStrings__Default` with a SQL server from
the decommissioned subscription — taking a working app down.

Verify the new revision took traffic, then verify behaviour rather than health:

```powershell
az containerapp revision list -n quotes-api-dev -g rg-quotes-dev `
  --query "[?properties.active].{name:name, traffic:properties.trafficWeight, healthy:properties.healthState}" -o json

$ui = "https://black-sea-0f5ad2a00.5.azurestaticapps.net"
curl.exe -s -o NUL -w "collections %{http_code}`n" "$ui/api/collections"   # 401
curl.exe -s -o NUL -w "quotes      %{http_code}`n" "$ui/api/quotes"        # 401
curl.exe -s -o NUL -w "ui          %{http_code}`n" "$ui/"                  # 200
```

Healthy and 100% traffic says the container started. It does not say the change
shipped. On day 32 the check that mattered was `collections` going from 200 to
401, because that was the defect being fixed — a revision can be perfectly
healthy and still be the old code.

## The deployed environment

```
ConnectionStrings__Default
Database__Provider
Database__SchemaBootstrap
ASPNETCORE_ENVIRONMENT
Jwt__Key
AZURE_CLIENT_ID
APPLICATIONINSIGHTS_CONNECTION_STRING
ServiceBus__FullyQualifiedNamespace
Auth__AdminEmails__0
```

`Jwt__Key` being present is worth checking after any change to the container's
configuration. Without it the app generates an ephemeral signing key per
process and logs a warning, which means every restart silently invalidates
every token already issued — users see 401s that nothing in the logs explains.

`ASPNETCORE_ENVIRONMENT` is `Production`, which is what keeps the diagnostics,
OpenAPI and demo endpoints from registering at all. They 404 rather than 401,
so the route list is not discoverable. See `Diagnostics:Enabled` in
`QuotesApi/Program.cs`.

## Infrastructure and the web app

Infrastructure is Bicep under `infra/`, deployed by `.github/workflows/deploy-infra.yml`
and validated by `.github/workflows/infra.yml`. The web app is deployed by
`.github/workflows/deploy-swa.yml`. Neither of those builds the API image.
