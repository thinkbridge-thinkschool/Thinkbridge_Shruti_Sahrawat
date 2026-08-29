# Deploy runbook — quotes-ui to Azure Static Web Apps

Exact commands, in order, for you to run yourself (PowerShell or your usual
shell, on your machine — not something I can run from this sandbox: see
`BRIEF-DEPLOY.md` for why). Paste back the marked output after each
numbered step; I'll use it to fill in `VERIFICATION-DEPLOY.md` and fix
forward if a step errors.

Placeholders you still fill in: `<SWA_NAME>` (pick one, e.g.
`quotes-ui-swa`). Resource group and subscription are filled in below as
real values, not placeholders — see "0" and "1".

The order below matters: the Static Web App and its deploy token have to
exist *before* you push, or the workflow's first run just fails on a
missing secret.

## 0. The Week-1 API was found torn down, then rebuilt — history, for the record

The original URL (`quotes-api.politesea-83b94ff4...`) turned out to have no
DNS record at all, and `azd env get-values` confirmed
`SERVICE_QUOTES_API_RESOURCE_EXISTS="false"` — the whole `thinkschool-dev2`
environment had been torn down, not just the app. `azd up` re-provisioned
it: resource group `rg-thinkschool-dev2`, region `southindia`, in **3
minutes 53 seconds**. The current live URL is
`https://quotes-api.blacksand-b575aaa0.southindia.azurecontainerapps.io/`
— every reference to the old URL in this repo's docs has been updated to
match. Confirm it's actually answering before wiring a new frontend to it:

```powershell
curl.exe -i https://quotes-api.blacksand-b575aaa0.southindia.azurecontainerapps.io/health
```

**Paste back:** the status line.

## 1. Your Azure context — already known from the `azd up` run

Resource group: `rg-thinkschool-dev2`. Subscription:
`109b67f4-3ed5-413c-bcb0-62c54340b387` (`Azure subscription 1`, tenant
`03490f7a-f873-47af-9963-ae925b4871b8`). Both filled in directly below —
no more `<RG>`/`<SUB>` placeholders in this file. Worth a quick sanity
check anyway before proceeding, since a resource group is exactly the kind
of thing that's cheap to double-check and expensive to have gotten wrong
three steps later:

```powershell
az containerapp list -o table
```

**Paste back:** confirms `quotes-api` shows up in `rg-thinkschool-dev2`.

## 2. Create the Static Web App (Standard plan — required for a linked backend)

```powershell
az staticwebapp create `
  --name <SWA_NAME> `
  --resource-group rg-thinkschool-dev2 `
  --sku Standard `
  --location "Central US"
```

Static Web Apps only deploys to a fixed list of regions, and South India
(where the container app lives) isn't one of them — the SWA's region only
affects where its build/deploy pipeline runs, not where it serves traffic
from, so this is fine. If `Central US` is rejected, the error will list the
valid ones; paste that back and I'll pick from it.

**Paste back:** the full JSON output, especially `"defaultHostname"` — that's
your live URL.

## 3. Get the deployment token and add it as a GitHub secret

```powershell
az staticwebapp secrets list --name <SWA_NAME> --resource-group rg-thinkschool-dev2 --query "properties.apiKey" -o tsv
```

Add it as a repository secret named `AZURE_STATIC_WEB_APPS_API_TOKEN`:
Settings → Secrets and variables → Actions → New repository secret, in the
`thinkbridge-thinkschool/Thinkbridge_Shruti_Sahrawat` repo on GitHub — or,
if you have the `gh` CLI signed in:

```powershell
gh secret set AZURE_STATIC_WEB_APPS_API_TOKEN --body "<paste the token>"
```

**Paste back:** just confirmation it's set — never paste the token itself
into this chat.

## 4. Commit and push the files already sitting in your working copy

I couldn't push these myself — this sandbox's git proxy only has
credentials for repos it's explicitly authorized against, and yours isn't
one of them (`403`, "not in this session's authorized repository set"). So
instead I wrote `staticwebapp.config.json`, `deploy-swa.yml`,
`BRIEF-DEPLOY.md`, `DEPLOY-RUNBOOK.md` (this file) and
`VERIFICATION-DEPLOY.md`, plus the Day 17 section in `README.md`, directly
into `C:\Users\dell\thinkschool\repo-live` through the bridge to your
machine — same files, just landed there instead of pushed through GitHub.

```powershell
cd C:\Users\dell\thinkschool\repo-live
git status
git add README.md .github/workflows/deploy-swa.yml BRIEF-DEPLOY.md DEPLOY-RUNBOOK.md VERIFICATION-DEPLOY.md quotes-ui/public/staticwebapp.config.json
git commit -m "Day 17 (draft): SWA config, linked-backend deploy workflow, brief + runbook"
git push origin main
```

This push touches `quotes-ui/**`, so it fires `deploy-swa.yml` — the first
real deploy.

**Paste back:** `git status` before the commit (so I can confirm nothing
unexpected is staged), then the push result.

## 5. Watch the deploy run

```powershell
gh run watch
```

Or the Actions tab on GitHub.

**Paste back:** pass/fail, and the run URL.

## 6. Link the Container App as the backend

```powershell
az staticwebapp backends link `
  --name <SWA_NAME> `
  --resource-group rg-thinkschool-dev2 `
  --backend-resource-id "/subscriptions/109b67f4-3ed5-413c-bcb0-62c54340b387/resourceGroups/rg-thinkschool-dev2/providers/Microsoft.App/containerApps/quotes-api" `
  --backend-region "southindia"
```

**Paste back:** the command's output, and separately, `az containerapp show
--name quotes-api --resource-group rg-thinkschool-dev2 --query
"properties.configuration.ingress.corsPolicy"` — confirming CORS is still
unset, since the linked backend needs none (same-origin through the SWA
proxy, see `BRIEF-DEPLOY.md`).

## 7. Verify, live

Once steps 2–6 are done, tell me the `defaultHostname` from step 2 and I'll
open it myself (I already have your browser bridge working — I used it in
this same session to confirm your API's own domain didn't respond, so I can
use it again here) and check: the SPA loads, `/quotes` renders real data
through `/api/quotes`, a direct hit on
`quotes-api.blacksand-b575aaa0.southindia.azurecontainerapps.io` from a
plain unauthenticated request now gets refused (proof the lock-down is
real, not just documented), and I'll walk the page for anything a real
Lighthouse category would flag that's checkable without the actual
Lighthouse panel (a DevTools-only tool I can't drive through this bridge).
You running the real Lighthouse audit (Chrome DevTools → Lighthouse, or
`npx lighthouse <url> --view`) and pasting the four category scores back is
still the one thing only you can do here.

## 8. Custom domain (skipped for this submission)

You chose the default `*.azurestaticapps.net` domain. If you want a real
domain later: `az staticwebapp hostname set --name <SWA_NAME>
--resource-group rg-thinkschool-dev2 --hostname <yourdomain>`, plus a CNAME (or ALIAS/ANAME
at the zone apex) at your DNS provider pointing at `<SWA_NAME>.
<region>.azurestaticapps.net`. Ask me when you're ready and I'll write the
exact record.
