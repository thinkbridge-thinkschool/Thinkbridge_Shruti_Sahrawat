# The brief — deploy to Azure Static Web Apps

The prompt given to the agent, before any deploy config existed. Day 17.

---

Ship `quotes-ui` to Azure Static Web Apps, live, calling the real Week-1 API
(`QuotesApi`, already running on Azure Container Apps —
`quotes-api.blacksand-b575aaa0.southindia.azurecontainerapps.io`, South
India, commit `a5894be`) with zero stored secret anywhere in the repo or in
app settings. Lighthouse ≥ 95. No custom domain for this submission — the
default `*.azurestaticapps.net` domain is fine.

**Auth model, decided up front rather than left to you to guess:** a linked
backend, not a hand-rolled Managed Identity token exchange. Static Web Apps
can link an Azure Container App as its backend
([docs](https://learn.microsoft.com/en-us/azure/static-web-apps/apis-container-apps)).
Once linked, the SPA keeps calling the same relative `/api/*` paths it
already calls today — no code change to `quotes-store.ts` or any of the
other four files that build a `/api/quotes...` URL — and Azure adds an
identity provider named `Azure Static Web Apps (Linked)` on the container
app that accepts only traffic proxied through that specific Static Web App.
No API key, no client secret, no bearer token either side has to mint or
store. I picked this over a proxy Azure Function minting a real Entra ID
token because it's zero new compute, zero new Entra app registration, and
the frontend code doesn't change at all — the tradeoff is it's Azure's
platform-level trust, not a token you can inspect on the wire, and it needs
the Standard plan (not the free tier).

Three things, all against the real repo:

**`staticwebapp.config.json`** in `quotes-ui/public/` (so the Angular build
copies it into `dist/quotes-ui/browser/`, the SWA output root). SPA fallback
to `index.html` for the client-side router, excluding `/api/*` and static
asset extensions — a route rewrite or reload on `/quotes/42` must not swallow
the API path the way a naive catch-all would. Long-lived immutable caching
on the hashed JS/CSS Angular already content-hashes, `no-cache` on
`index.html` itself so a redeploy is seen on the next load, and the security
headers that don't need a live browser to prove safe (`X-Content-Type-Options`,
`X-Frame-Options`, `Referrer-Policy`, a locked-down `Permissions-Policy`).

**CI/CD** (`.github/workflows/deploy-swa.yml`), separate from `ci.yml`'s
three .NET test legs and scoped with `paths` so a QuotesApi-only push
doesn't rebuild and redeploy the SPA. Standard `Azure/static-web-apps-deploy`
action, `output_location: dist/quotes-ui/browser` for Angular's new
application builder's split output, `skip_api_build: true` because the API
is a linked Container App, not a Functions folder living in this repo.

**The link itself** — `az staticwebapp backends link` — isn't something
either of us can run from this sandbox: I have no Azure login here, and the
bridge to your machine reaches an isolated Linux VM with no `az`/`azd`
installed, not your real Windows shell where those are already logged in.
Write the exact commands, in order, for me to run on my own machine, with
placeholders only where you genuinely don't know the value (resource group,
subscription) — don't guess a resource group name from old docs that may be
stale.

## Verify before handing back

I can't run Lighthouse or curl the live URL from this sandbox either — no
egress to `azurestaticapps.net` or the container app's own domain, confirmed
against the agent proxy's own status endpoint, not assumed. So the
verification log has two halves: what you can prove right now without a
browser or Azure access (the frontend still builds clean and its 70 tests
still pass with these files added, the API genuinely has no auth or CORS
today so neither is a regression, why CORS specifically isn't needed once
traffic is same-origin through the linked backend), and a checklist for what
I run and paste back to you afterward (the actual live URL, `/api/quotes`
answering through it, a direct hit on the container app's own domain now
getting refused, the Lighthouse score). Don't claim the second half; leave it
open with what would confirm it.

## Reasoning goes in comments

Why the linked-backend model over a literal Managed Identity token. Why
`skip_api_build: true`. Why the workflow is `paths`-scoped separately from
`ci.yml` instead of one workflow doing both. I will be asked to defend each
one.

## Where this diverges from the literal brief wording

The Day 17 brief text asks for two things this deploy does not literally
have: a custom domain, and API calls authenticated by a real Managed-Identity
token. Both are deliberate scope decisions, made with you, not oversights
found after the fact.

**Custom domain.** Skipped for this submission. The live URL
(`https://white-bush-08e3cd710.7.azurestaticapps.net`) is a real, live,
fully-HTTPS Azure endpoint — a custom domain on top of it is a DNS/CNAME
step (`az staticwebapp hostname set` plus a CNAME or ALIAS record at a
registrar) that doesn't touch the auth model or the Lighthouse score being
graded here. Nothing about the current setup blocks adding one later —
`DEPLOY-RUNBOOK.md` step 8 has the exact commands ready for whenever a real
domain is available.

**Managed Identity.** The brief's exact wording is "the code that
authenticates to your Week-1 API via Managed Identity" and "the API calls
use a managed-identity token." What's actually running instead is Azure's
SWA-linked-backend model (see above, and the live verification section in
`VERIFICATION-DEPLOY.md`): the container app's `Azure Static Web Apps
(Linked)` identity provider refuses every request that didn't arrive through
this specific Static Web App — confirmed live with a direct `curl` returning
`401` with a `www-authenticate: Bearer` header. That genuinely satisfies the
brief's other line, "zero stored secret anywhere in the repo or in app
settings" — there is no key, token, or credential anywhere in this deploy.
It does not satisfy the literal ask of an MI-minted bearer token being
validated, because there is no token in this design at all; the trust is
platform-level routing, not a credential exchange.

A real MI flow would additionally need: a system-assigned identity enabled
on the Static Web App, an Entra ID app registration (or exposed API scope)
on the container app's side, an auth library such as `Microsoft.Identity.Web`
wired into `QuotesApi/Program.cs` to validate an incoming bearer token's
issuer and audience, and a role assignment granting the SWA's identity
permission to call it. The concrete thing that would then break: any change
to that app registration, its allowed audiences, or the identity's role
assignment would take the API down with 401s until the config was fixed —
exactly the class of failure the current linked-backend model doesn't have,
since it has no token to expire, rotate, or misconfigure. That's a
materially larger, riskier change than what shipped here, and one we chose
not to make this late in the exercise rather than rush untested.

---

## What I changed after reading the output

Written up in [`VERIFICATION-DEPLOY.md`](VERIFICATION-DEPLOY.md) and
[`DEPLOY-RUNBOOK.md`](DEPLOY-RUNBOOK.md). Short version: everything code-side
was buildable and testable without Azure access, so it's done and verified
as far as this sandbox can go. I couldn't push it to `origin` myself either
— this sandbox's git proxy has no credential for this repo — so these files
are written directly into the working copy on your machine instead, and
`DEPLOY-RUNBOOK.md` step 4 is `git add`/`commit`/`push` from there. Everything
that needs a real Azure login — creating the Static Web App, wiring the
GitHub secret, linking the backend, the live Lighthouse run — is in the
runbook as exact commands, not run yet.
