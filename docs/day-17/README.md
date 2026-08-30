[← Back to full README](../../README.md)

## Day 17 — Deploy to Azure Static Web Apps

[`BRIEF-DEPLOY.md`](../../BRIEF-DEPLOY.md) asked for `quotes-ui` live on Azure
Static Web Apps, calling the real Week-1 API (`QuotesApi`, already on Azure
Container Apps since Day 5) with zero stored secret anywhere in the repo or
app settings.

**The auth model, decided before any config was written.** Static Web Apps
can link an Azure Container App as its backend
([docs](https://learn.microsoft.com/en-us/azure/static-web-apps/apis-container-apps)):
once linked, the SPA keeps calling the same relative `/api/*` paths it
already calls today — no code change anywhere in `quotes-ui` — and Azure
locks the container app to only accept traffic proxied through that one
Static Web App, via an identity provider it adds automatically. No API key,
no client secret, no bearer token either side mints or stores. Chosen over a
proxy Azure Function minting a real Managed Identity token because it's zero
new compute and zero new Entra app registration — the tradeoff is it's
Azure's platform-level trust rather than an inspectable token, and it needs
the Standard plan.

**`staticwebapp.config.json`** lives in `quotes-ui/public/` so Angular's
build copies it into `dist/quotes-ui/browser/`. SPA fallback to
`index.html` excluding `/api/*` and static asset extensions, long-lived
immutable caching on the content-hashed JS/CSS, `no-cache` on `index.html`
itself, and the security headers checkable without a live browser.

**CI/CD** is [`deploy-swa.yml`](../../.github/workflows/deploy-swa.yml), separate
from `ci.yml` and `paths`-scoped to `quotes-ui/**` so an API-only push
doesn't rebuild and redeploy the frontend.

This sandbox has no Azure login and no route to `az`/`azd` at all — not
here, and not through the bridge to the development machine, which reaches
an isolated VM without those tools installed. Everything code-side is
written, built, and tested; everything that needs a real Azure session
(creating the Static Web App, linking the backend, the live Lighthouse run)
is [`DEPLOY-RUNBOOK.md`](../../DEPLOY-RUNBOOK.md) as exact commands, not yet run.
Full write-up, including what could and couldn't be verified from here and
why, in [`VERIFICATION-DEPLOY.md`](../../VERIFICATION-DEPLOY.md).
