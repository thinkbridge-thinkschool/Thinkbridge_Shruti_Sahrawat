# Verification log — deploy to Azure Static Web Apps

Day 17. Draft — half of this can be proven from this sandbox alone, half
needs the commands in `DEPLOY-RUNBOOK.md` run first. Updated once those come
back rather than claimed now.

## What was exercised, in this sandbox, before any deploy config existed

**Frontend still builds and tests clean with the new files added.**
`ng build --configuration production` from a clean clone fast-forwarded to
`d6fbd48`:

```
Application bundle generation complete. [5.771 seconds]
▲ [WARNING] src/app/quotes-list.css exceeded maximum budget. Budget 4.00 kB was not met by 797 bytes with a total of 4.80 kB.
```

One pre-existing budget warning, not an error, not something this change
introduced. `ng test --watch=false`: **70 tests passed, 11 files, 0
failures** — the same suite Day 16's verification log reported, unchanged by
adding `staticwebapp.config.json` (a `public/` asset, not app code) or the
new workflow file.

**The API's missing auth and missing CORS are read from the source, not
assumed.** `QuotesApi/Program.cs`, all 170 lines, has no
`AddAuthentication`, `AddAuthorization`, `RequireAuthorization`, or
`AddCors` call anywhere — grep for `Cors`/`CORS` across the whole
`QuotesApi/` tree returns nothing. That's not a gap this deploy needs to
close: the chosen architecture (SWA-linked backend, see `BRIEF-DEPLOY.md`)
makes every request to `/api/*` same-origin from the browser's point of
view — the browser only ever talks to the Static Web App's own domain, and
Azure proxies server-to-server from there to the container app. CORS exists
to police cross-origin requests; there isn't one in this design. If a
future day calls the container app directly from a different origin
(mobile app, a second frontend), that's the point CORS would need to exist.

**What I could not run here, and why — not a workaround, a hard block.**
`dotnet build`/`dotnet test` on `QuotesApi`: `api.nuget.org` is blocked by
this sandbox's egress policy (confirmed via the proxy's own status
endpoint — `connect_rejected`, "gateway answered 403 to CONNECT", not a
timeout or a flaky retry). Reaching the live API directly (curl, or a
Lighthouse run against it): same policy, different host, same
`connect_rejected`. `az`/`azd`, at all: not installed anywhere I have shell
access — not in this sandbox, and not in the isolated Linux VM the device
bridge reaches on your machine, which is a separate environment from your
real Windows shell where they're actually logged in.

**The Week-1 API was actually down, and it needed a real diagnosis, not a
guess.** The original URL (`quotes-api.politesea-83b94ff4...`) had no DNS
record at all — `curl.exe: Could not resolve host` from your own terminal —
and `azd env get-values` confirmed why:
`SERVICE_QUOTES_API_RESOURCE_EXISTS="false"`. The whole `thinkschool-dev2`
environment had been torn down, not just the container app; `az group
list` showed no project resource group left, only Azure's own
auto-created `DefaultResourceGroup-CID`. Every attempt to reach it myself —
through this sandbox's network and through the browser bridge to your
machine — failed the same way regardless of cause, which is exactly why it
couldn't be diagnosed from here: a permission block, a DNS failure, and an
egress policy all look identical from the outside. `azd up` re-provisioned
the whole environment in 3 minutes 53 seconds (`rg-thinkschool-dev2`,
`southindia`), and the new URL —
`https://quotes-api.blacksand-b575aaa0.southindia.azurecontainerapps.io/` —
answers `GET /health` with **200 OK, "Healthy"**, confirmed from your own
terminal, not assumed. Every doc in this repo that referenced the dead URL
has been updated to the live one.

## States and edges exercised

| State / edge | How it was forced | Result |
|---|---|---|
| Production build, new config present | `ng build --configuration production` | succeeds, one pre-existing CSS budget warning, no new warnings |
| Unit suite, new config present | `ng test --watch=false` | 70/70 pass, 11 files |
| API auth surface | read all of `Program.cs`, grepped for `Cors`/`Authoriz` | no auth, no CORS — matches README's own claim, not a new finding |
| Live API reachability | `azd env get-values` showed `SERVICE_QUOTES_API_RESOURCE_EXISTS="false"`; `azd up` re-provisioned (`rg-thinkschool-dev2`, 3m53s); `curl.exe /health` from your terminal on the new URL | **200 OK, "Healthy"** — confirmed, not assumed |
| Git sync | `git fetch` + `git log --left-right --count main...origin/main` before touching anything | 0 local-only commits, 1 origin-only commit (`d6fbd48`) — fast-forwarded, no merge, no risk of clobbering work done on your machine since the last pull |

## What this can't prove yet

**Everything that needs Azure access.** The Static Web App doesn't exist
yet, so there is no live URL, no Lighthouse score, no proof the linked
backend actually locks the container app down the way the docs describe,
and no proof `staticwebapp.config.json`'s SPA fallback and cache headers
behave correctly once actually served by Azure's edge rather than just
copied into a local `dist/` folder. `DEPLOY-RUNBOOK.md` steps 2–7 are what
closes each of these; this file gets a second pass once they're run.

**Lighthouse specifically.** I have no DevTools/Lighthouse-panel access
through the browser bridge, only navigation, page text, and network
requests — the four category scores in the brief's "Lighthouse ≥ 95" line
have to come from you running the actual audit and pasting the numbers
back.

**CSP.** Deliberately not added to `staticwebapp.config.json`. The app
loads two Google Fonts stylesheets from `fonts.googleapis.com` /
`fonts.gstatic.com` (`index.html`), and a CSP tight enough to matter for
Lighthouse's Best Practices score but wrong for that would break the fonts
silently with no way for me to catch it without a live page to load —
noted as a gap, not fixed blind.

## What breaks if the architecture changes

**A second frontend or a mobile client needs the same API**, one that can't
be proxied through this Static Web App's linked-backend trust. That's
exactly the point `AddCors` (or the real Managed Identity / Entra app
registration path this brief explicitly chose *not* to take) becomes
necessary rather than optional — today's zero-auth, zero-CORS API only
stays safe because the *only* path to it that matters is the one Azure
locks to this specific SWA.

**The container app's region or resource group changes.** `--backend-region
southindia` in `DEPLOY-RUNBOOK.md` step 6 and the resource id built from
`<RG>` are both point-in-time facts pulled from step 1's `az containerapp
list`; nothing here re-discovers them automatically if the API gets
redeployed somewhere else later.
