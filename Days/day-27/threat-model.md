# STRIDE-lite threat model — the Quotes capstone

Scope: everything this repo builds and deploys, as it exists on the day this
was written. Not a generic OWASP checklist — every row below names a specific
file, endpoint or resource in this repository, and every "today" column says
what the code actually does rather than what it ought to.

## The system

| Component | What it is | Where it runs |
|---|---|---|
| `quotes-ui` | Angular SPA | Azure Static Web Apps, public |
| `QuotesApi` | Minimal-API service, the only thing with a database connection | Container App, public ingress |
| Azure SQL | `quotesdb` — users, quotes, collections, outbox | Public endpoint, firewall-gated |
| Service Bus | topic `quote-events`, subscriptions `search-indexer` and `audit-log` | Public endpoint |
| Key Vault | holds the JWT signing key (Day 25) | Public endpoint, RBAC-authorised |
| Redis | optional L2 cache; registers only when a connection string is present | Local only |
| `Quotes.Outbox` relay, `Quotes.Worker` | poll the outbox, publish, consume | **Local only — not deployed** |
| User-assigned managed identity | how the API reaches SQL, Service Bus and Key Vault | — |

The relay and worker not being deployed matters for this model: several
threats below are latent rather than live, and become live the day those two
get hosted.

## Trust boundaries

1. **Browser → API.** Anonymous internet to public ingress. The widest boundary.
2. **API → data tier.** Managed identity over the public internet to SQL,
   Service Bus and Key Vault. No secrets cross it (Day 25), but the traffic
   does traverse public endpoints.
3. **API → outbox → relay → Service Bus → worker.** A store-and-forward
   boundary. Day 26 showed trace context does not survive it by itself; nor
   does the identity of whoever caused the message.
4. **CI → Azure.** GitHub Actions holds Owner on the subscription through a
   federated identity (no stored secret), and can create and destroy every
   resource.

## S — Spoofing

| Threat | Today | Gap |
|---|---|---|
| Forged bearer token | JWT validated with issuer, audience, lifetime and signing key all on, `ClockSkew` zero (`Program.cs`) | — |
| Signing key theft | Key is a Key Vault reference, not an app setting (Day 25) | Key never rotates; no `kid`, so rotation means invalidating every live token at once |
| Credential stuffing on `/api/auth/login` | BCrypt hashing, anonymous by design | **No rate limiting.** An attacker can try passwords as fast as the network allows |
| Service-to-service spoofing | Managed identity, no shared secrets | — |

## T — Tampering

| Threat | Today | Gap |
|---|---|---|
| Editing another user's quote | Ownership checked on DELETE (`CanAccessQuoteOwnedBy`) | — |
| **Anonymous control of fault injection** | `/api/upstream/mode/{mode}`, `/api/upstream/reset`, `/api/resilience/*` are all `AllowAnonymous` | Anyone who can reach the deployed API can flip its upstream into permanent-failure mode |
| **Anonymous cache reset** | `/api/cache/reset` is `AllowAnonymous` | Anyone can clear cache metrics, destroying the evidence in any cache investigation |
| Manual edit of a stack-managed resource | `denySettings: denyDelete` (Day 24) | Blocks deletion, **not** modification — Day 24 Finding 19 showed drift surviving three re-provisions |
| SQL injection | EF Core parameterises; the one Dapper path uses parameters | — |

## R — Repudiation

| Threat | Today | Gap |
|---|---|---|
| "I didn't post that quote" | Correlation ID middleware, Serilog, App Insights, and Day 26's traceparent stitched through the outbox | Logs record the request, not the acting user — no `sub` claim on the log scope |
| Deleted audit trail | `audit-log` subscription exists | Nothing consumes it in production, because the worker is not deployed |

## I — Information disclosure

| Threat | Today | Gap |
|---|---|---|
| Exception detail leaking to callers | `ExceptionHandlingMiddleware` returns ProblemDetails, detail suppressed outside Development | — |
| Secrets in config | Zero connection-string secrets; managed identity everywhere; JWT key via Key Vault reference (Day 25) | — |
| **Data tier on public endpoints** | SQL firewall + Entra-only auth; Key Vault RBAC; Service Bus RBAC | All three are reachable from the internet and defended by credentials alone. **The private endpoint work below adds a private path alongside this one - it does not close it; see "Accepted, and why"** |
| **Every signed-in user sees every quote** | Deliberate and documented (`EndpointExtensions.cs`) | Correct for this app; would be a flaw in a multi-tenant one |
| No OpenAPI document | — | Nothing to leak, but also nothing to review — the surface is undocumented, which is its own risk |

## D — Denial of service

| Threat | Today | Gap |
|---|---|---|
| **`/api/demo/queue-work?delayMs=`** | Anonymous, `delayMs` unbounded | Anyone can queue unbounded background work. The clearest DoS in the app |
| **`/api/profiling/author-stats-slow`** | Anonymous, deliberately slow by design | A free CPU-and-IO amplifier for anyone who finds it |
| Unbounded page size | Clamped: `Math.Min(size, 100)` | — |
| Oversized request body | Kestrel default 30 MB | Never narrowed for an API whose largest legitimate body is about 1 KB |
| Retry storms against the upstream | Day 22's full Polly pipeline, total budget included | — |
| Cost exhaustion | Container Apps scale rules bounded by `apiMaxReplicas` | — |

## E — Elevation of privilege

| Threat | Today | Gap |
|---|---|---|
| Ordinary user acting as admin | Role claim type stated explicitly on both sides, so a mismatch cannot silently grant | — |
| Reaching an authorised endpoint anonymously | `/api/quotes` group carries `RequireAuthorization()` | — |
| **Diagnostic endpoints as a privilege island** | The demo, profiling, cache and resilience endpoints sit outside the auth model entirely | They are not "low privilege" — they are *no* privilege, on the same host as the authorised ones |
| CI compromise | Federated identity, no stored secret, prod gated on a required reviewer | The identity holds **Owner** on the whole subscription, because the template creates role assignments. Blast radius of a compromised workflow is total |

## What this day changes

1. The demo, profiling, cache and resilience endpoints stop being reachable in
   Production — the single largest reduction in attack surface here, and it
   costs nothing.
2. Rate limiting on the anonymous auth endpoints, closing the credential
   stuffing gap.
3. A request body cap proportional to what the API actually accepts.
4. An OpenAPI document with the bearer scheme declared, and `/v1` versioning,
   so the surface is reviewable and can change without breaking callers.
5. A VNet, private DNS zones, and private endpoints for SQL, Key Vault and (on Premium) Service Bus - real resources, proven by DNS resolution from inside the VNet, alongside the public endpoint rather than instead of it. See Days/day-27/private-endpoints.md for why closing the public side isn't possible yet.

## Accepted, and why

- **The app tier cannot join the VNet.** Container Apps can only reach a
  private endpoint from a VNet-injected managed environment. This subscription
  allows exactly one managed environment and the live Day 5 app holds it
  (Day 24, Finding 7). Creating a second is not possible; destroying the live
  one to prove a point is not acceptable. So the private endpoints are real and
  proven by DNS resolution from inside the VNet, and the API keeps reaching SQL
  over the public endpoint until that quota changes.
- **Service Bus private endpoints need Premium.** Standard does not support
  them at all, so dev cannot have one. The template makes it conditional on the
  SKU rather than pretending otherwise.
- **No JWT key rotation.** Out of scope for a day; named so it is not mistaken
  for handled.
- **CI holds Owner.** Narrowing it means splitting role-assignment creation out
  of the template, which is a larger change than this day.
