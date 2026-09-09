# Day 25 — Identity end-to-end

The exercise: no connection-string secrets anywhere, managed identity on the
API→SQL and API→Service Bus paths, Entra ID for app auth, Key Vault
references for whatever config is left, and proof that app settings hold zero
plaintext secrets.

Most of the identity work was already standing before this day started, which
is worth saying up front rather than presenting Day 23 and 24's output as new.
What Day 25 actually changed is one thing, and it is the thing the exercise
names: the last secret moved out of the application's own storage and behind
a Key Vault reference.

## What was already true before today

| Requirement | Where it was already met | Since |
|---|---|---|
| MI on API→SQL | `Authentication=Active Directory Managed Identity;User Id=<clientId>` in `modules/api.bicep` — no password in the connection string because the server has no password to give | Day 23, proven at runtime Day 24 |
| MI on API→Service Bus | `ServiceBus__FullyQualifiedNamespace` + `AZURE_CLIENT_ID`, with Data Sender/Receiver role assignments in `modules/servicebus.bicep` | Day 23 |
| No SQL password *can* exist | `azureADOnlyAuthentication: true`, and no `administratorLogin`/`administratorLoginPassword` in the template at all | Day 23 |
| No Service Bus SAS key *can* exist | `disableLocalAuth: true` on the namespace — the keys are not merely unused, they are not issued | Day 23 |
| JWT key not a plaintext env var | `@secure()` param → container app secret → `secretRef` | Day 24, Finding 16 |

The pattern is worth naming because it is the actual lesson of the exercise:
every row above is a case where the credential does not exist rather than one
where it exists and is well hidden. A password that was never issued cannot
leak from app settings, a config file, a deployment history, a screenshot, or
a support ticket. That is a different and much stronger property than
encryption at rest, and it is why "use managed identity" is worth the setup
cost over "store the password carefully".

## What Day 25 changed

One secret was left: the HMAC-SHA256 key QuotesApi signs its own access tokens
with. It cannot be a managed identity, because it is not a credential *to*
anything — it is the key the app uses to prove that a token it issued is one
it issued. There is nothing to federate with.

Day 24 put it in the container app's own secret store and said plainly at the
time that this was "a smaller guarantee than Key Vault, and the right-sized
one for a single signing key with no rotation story yet." Day 25 is the day
that stops being the right size.

**Before** (`modules/api.bicep`, through Day 24):

```bicep
secrets: [
  {
    name: 'jwt-key'
    value: jwtSigningKey     // the key itself, stored in the container app
  }
]
```

**After**:

```bicep
secrets: [
  {
    name: 'jwt-key'
    keyVaultUrl: jwtSecretUri              // versionless URI, not a value
    identity: keyVaultIdentityResourceId   // the MI that reads it
  }
]
```

The `env` entry did not change at all, and that is the point — the container
still reads `Jwt__Key` from `secretRef: 'jwt-key'`. What changed is where that
secret gets its value from, which the application neither knows nor needs to.

What this buys over the Day 24 arrangement, concretely, since "use Key Vault"
is often asserted rather than justified:

- **Ownership.** A container app secret is owned by the container app: anyone
  with write access to the app can read it back. A Key Vault secret is a
  separate resource with its own RBAC, so "who can deploy this app" and "who
  can read this key" become two different questions with two different
  answers.
- **Rotation.** The URI is versionless, so writing a new version into the
  vault is picked up without redeploying the app. Under Day 24's arrangement
  rotating the key meant a new deployment, which means rotation competes with
  release scheduling.
- **Audit.** Vault reads are logged as data-plane operations against a
  resource that exists to be audited. A container app secret read is not an
  event anywhere.

New in this day: [`infra/modules/keyvault.bicep`](../../infra/modules/keyvault.bicep)
— the vault, the secret, and one role assignment (`Key Vault Secrets User`,
scoped to the vault rather than the resource group, because a group-scoped
grant would cover every vault the group ever gains).

## On "Entra ID for app auth"

The task names three things: managed identity for the Azure paths, Entra ID
for app auth, Key Vault for remaining config. Two of them are unambiguous.
The third needs a decision stated rather than quietly interpreted, so:

**The API's *service* authentication is Entra ID and always has been.** A
managed identity is an Entra ID service principal; `azureADOnlyAuthentication`
on SQL means Entra ID is the only accepted issuer; the Service Bus role
assignments are Entra ID RBAC. Nothing in this system authenticates to Azure
any other way.

**The API's *end-user* sign-in is not Entra ID, and was left that way
deliberately.** QuotesApi has its own registration and login — BCrypt password
hashing, its own JWT — built across Days 13–16, with the Angular client's
guards, interceptor and session handling built against it and roughly ninety
tests covering it. Replacing that with Entra ID sign-in would rewrite
`AuthController`, `JwtTokenService`, the frontend login flow and the route
guards, and would invalidate the tests that are the evidence for those days.

That is a real trade rather than an avoidance: an exercise that says "identity
end-to-end" has a fair claim on end-user identity too. What tipped it is that
the work is already submitted and graded, the exercise's own checklist asks
for "the MI wiring + a Key Vault reference" and proof of no plaintext secrets
— all three of which are about service identity — and a migration that breaks
four days of graded work to satisfy a phrase in the prose is a bad trade at
this point in the course. Named here so a mentor can disagree with the call
rather than have to detect it.

## Proof: zero plaintext secrets in app settings

Deployed for real to `rg-quotes-dev`, borrowing the live app's managed
environment (Day 24's `-ReuseManagedEnvironment`, still needed — the
subscription's one-environment cap has not moved). Provisioned in 4m54s:

```
  (✓) Done: Resource group: rg-quotes-dev (4.038s)
  (✓) Done: Azure SQL Server: quotes-sql-dev-e6oljhc2krrhe (7.361s)
  (✓) Done: Service Bus Namespace: quotes-sb-dev-e6oljhc2krrhe (2.365s)
  (✓) Done: Key Vault: kv-dev-e6oljhc2krrhe (1m29.039s)
  (✓) Done: Container App: quotes-api-dev (18.061s)
```

### Every environment variable the container actually has

```
$ az containerapp show -n quotes-api-dev -g rg-quotes-dev \
    --query "properties.template.containers[0].env" -o json
[
  { "name": "ConnectionStrings__Default",
    "value": "Server=tcp:quotes-sql-dev-e6oljhc2krrhe.database.windows.net,1433;Database=quotesdb;Authentication=Active Directory Managed Identity;User Id=fb678f4e-ac66-498a-b18a-6c53bb091b48;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;" },
  { "name": "Database__Provider",              "value": "SqlServer" },
  { "name": "Database__SchemaBootstrap",       "value": "EnsureCreated" },
  { "name": "ASPNETCORE_ENVIRONMENT",          "value": "Production" },
  { "name": "Jwt__Key",                        "secretRef": "jwt-key" },
  { "name": "AZURE_CLIENT_ID",                 "value": "fb678f4e-ac66-498a-b18a-6c53bb091b48" },
  { "name": "ServiceBus__FullyQualifiedNamespace",
    "value": "quotes-sb-dev-e6oljhc2krrhe.servicebus.windows.net" }
]
```

That is the complete list — seven entries, nothing elided. Going through them
individually, because "no secrets" is a claim that deserves to be checked
rather than asserted:

- **`ConnectionStrings__Default`** is a connection string in full public view,
  and it is not a secret, because `Authentication=Active Directory Managed
  Identity` means there is no password in it and no password that *could* be
  in it — the server runs `azureADOnlyAuthentication: true` and has no SQL
  login to hold one. The `User Id` is the managed identity's **client ID**,
  which is an identifier, not a credential: knowing it lets you name the
  identity, not authenticate as it. Anyone who reads this string learns the
  server's hostname and the database name, both of which are also in the
  Azure portal.
- **`Database__Provider`**, **`Database__SchemaBootstrap`**,
  **`ASPNETCORE_ENVIRONMENT`** — behavioural switches, no security content.
- **`Jwt__Key`** carries **`secretRef`**, not `value`. This is the entire
  exercise in one line: the only genuinely secret piece of configuration is
  the one entry in this array that does not contain its own value.
- **`AZURE_CLIENT_ID`** is the same client ID again, for the same reason.
- **`ServiceBus__FullyQualifiedNamespace`** is a hostname. Under Day 23's
  `disableLocalAuth: true` the namespace issues no SAS keys at all, so there
  is no connection-string-with-key form of this value in existence to leak.

### What the secret store holds

```
$ az containerapp secret list -n quotes-api-dev -g rg-quotes-dev -o json
[
  {
    "identity": "/subscriptions/.../resourceGroups/rg-quotes-dev/providers/Microsoft.ManagedIdentity/userAssignedIdentities/quotes-id-dev",
    "keyVaultUrl": "https://kv-dev-e6oljhc2krrhe.vault.azure.net/secrets/jwt-key",
    "name": "jwt-key"
  }
]
```

Worth being exact about this, because the prediction going in was slightly
wrong. The expectation was a `value` field containing an empty string —
masked. What actually comes back has **no `value` field at all**. There is
nothing to mask, because the container app is not storing a secret: it holds
a URI, an identity, and a name. The value lives in the vault and is fetched at
resolve time by the identity named on the secret. `az containerapp show` is
the tool an operator, an auditor or an attacker with read access would reach
for first, and it cannot print a key that the resource does not have.

Under Day 24's arrangement this same command returned a `value` field —
masked, but present, because the app genuinely held the key.

### The vault's access model

```
$ az keyvault show --name kv-dev-e6oljhc2krrhe --query "{...}" -o json
{
  "name": "kv-dev-e6oljhc2krrhe",
  "purgeProtection": null,
  "rbac": true,
  "softDeleteDays": 7
}
```

`rbac: true` is what makes the single `Key Vault Secrets User` assignment
meaningful rather than decorative — under the older access-policy model there
is no way to express "read this one secret" without also granting list over
the whole vault.

`purgeProtection: null` deserves a note, because Day 24's Finding 15 hit the
mirror image of it: `az sql server show` reports `azureADOnlyAuthentication`
as `null` when it is in fact `true`. Here `null` means not enabled, which is
correct and intended (`modules/keyvault.bicep` sets it to `null` rather than
`false` on purpose — Azure rejects an explicit `false` once a vault has ever
had it on). So the same `null` means opposite things on two different security
properties, and neither can be read as "off" or "on" without checking that
specific property's semantics. A dashboard that treats `null` as "not
configured" would be wrong about one of these two and right about the other.

### The proof that outranks all of the above

```
$ curl.exe -s https://quotes-api-dev.../health
Healthy
```

Configuration output proves wiring. This proves the wiring *works*.
`Program.cs` throws on startup in Production when `Jwt:Key` is missing or
under 32 UTF-8 bytes, and this container runs with
`ASPNETCORE_ENVIRONMENT=Production`. A `Healthy` response is therefore only
reachable if the container app resolved the Key Vault reference, authenticated
to the vault as `quotes-id-dev`, was granted access by the `Key Vault Secrets
User` assignment, received a valid key, and passed it to the app as
`Jwt__Key`. Every link in that chain is load-bearing; any one of them broken
returns a crash loop instead of `Healthy`.

## Findings

### Finding 1 — the predicted RBAC race did not happen, and that is not the same as it being safe

Going in, the expected failure was the container app resolving its Key Vault
reference before the `Key Vault Secrets User` assignment had propagated —
Container Apps resolves at create time, and RBAC propagation is eventual.
The retry never came: the deployment succeeded first time.

The reason is visible in the timings above. The vault took 1m29s to create,
and the role assignment is inside that same module, so by the time the
container app started its 18 seconds the assignment had had well over a
minute to propagate. That is comfortable, and it is also luck rather than
guarantee: nothing in the template *waits* for propagation, and a faster vault
creation or a slower directory would close that gap. Recording it as "worked
first time" without the caveat would be recording the wrong lesson.

### Finding 2 — the strict linter rejected a dependency that expressed a guarantee already made

The first draft of `main.bicep` added `dependsOn: [keyVault]` to the api
module, reasoning that the container app needs the *role assignment* to exist,
not merely the vault, and that depending on the module rather than on an
output would wait for everything inside it.

`bicep build` failed on it:

```
Error no-unnecessary-dependson: Remove unnecessary dependsOn entry 'keyVault'.
```

The linter is right and the reasoning was wrong. The api module reads
`keyVault.outputs.jwtSecretUri`, and a module's outputs are not available
until its nested deployment has completed — every resource in it, role
assignment included. The hand-written dependency restated what the output
reference already guaranteed. Left in, it would have been a comment asserting
a safety property that the code around it did not actually depend on it for,
which is the kind of thing that survives a refactor and then misleads someone.

Worth noting the linter caught this only because `bicepconfig.json` raises
linter rules to errors (Day 23). At default severity this is a warning that
scrolls past.

### Finding 3 — a `${...}` sequence inside a Bicep `@description` string is interpolation, not text

`modules/keyvault.bicep` documented why the vault name cannot follow the
naming shape the other resources use, and wrote that shape literally in the
description. Bicep read it as string interpolation and failed on four
undefined symbols:

```
BCP057: The name "prefix" does not exist in the current context.
BCP057: The name "kind" does not exist in the current context.
BCP057: The name "env" does not exist in the current context.
BCP057: The name "token" does not exist in the current context.
```

Trivial to fix and worth recording because of where it does *not* apply: the
same text in a `//` comment two lines above compiled fine. Bicep's
interpolation applies inside string literals, and a `@description` is a string
literal, so documentation written inside one is code. The same sentence is
safe in one place and a build error in the other.

### Finding 4 — Key Vault names break the naming convention every other resource in the stack follows

SQL and Service Bus both use `${namePrefix}-${kind}-${environmentName}-${resourceToken}`
— `quotes-sql-dev-e6oljhc2krrhe` is 28 characters and fine. Key Vault caps at
24, and the same shape gives `quotes-kv-prod-<13-char token>` at 27.

So the vault generates its name differently (`kv-${environmentName}-${resourceToken}`,
20–21 characters) and `main.bicep` says why at the point of divergence. This
is a small thing that is only small because it was caught at authoring time:
the length rule is enforced by Azure at deploy, not by `bicep build`, so the
failure mode was a prod deployment getting as far as the vault and stopping —
with SQL and Service Bus already standing, which is exactly the partial-stack
state Day 24's Finding 4 spent an afternoon on.

### Finding 5 — the vault outlived the teardown, and the name is deterministic

After the stack was torn down and `rg-quotes-dev` was gone, the vault was
still there:

```
$ az keyvault list-deleted --query "[?name=='kv-dev-e6oljhc2krrhe']" -o json
[
  {
    "name": "kv-dev-e6oljhc2krrhe",
    "scheduledPurge": "2026-09-16T04:02:36+00:00"
  }
]
```

Key Vault soft-delete cannot be turned off. Deleting a vault removes it from
the resource group and from every `az resource list`, but reserves the *name*
for the retention window — 7 days here, the minimum Azure allows and already
the shortest this template could ask for.

That reservation is a problem specifically because this stack's names are
deterministic. `resourceToken` is `uniqueString(subscription().id,
resourceGroupName, environmentName)`, chosen on Day 23 precisely so that
re-running the template for the same environment produces the same names
rather than looking like a brand-new stack to `what-if`. The consequence
nobody had traced until now: the next `azd provision -Environment dev` asks
Azure for `kv-dev-e6oljhc2krrhe`, which is exactly the name its own deleted
predecessor is holding, and the deployment fails on a conflict with itself.
Every other resource in the stack tolerates reuse of its name; the vault is
the first that does not.

Fixed with one command, which also confirms the diagnosis:

```
$ az keyvault purge --name kv-dev-e6oljhc2krrhe --location centralindia
$ az keyvault list-deleted --query "[?name=='kv-dev-e6oljhc2krrhe'].name" -o tsv
(empty)
```

**What has not been established is why the purge did not happen during
teardown.** Two candidates, and they carry different lessons:

- The teardown ran without `--purge`, in which case the documented command is
  correct and this is operator error.
- The teardown ran *with* `--purge` and it did not reach the vault, in which
  case there is a real gap: with `alpha.deployment.stacks` on, `azd down`
  delegates deletion to `az stack sub delete`, and azd's own purge step may
  only cover resources it tracks in its own deployment state rather than
  resources the stack owns. That would make the teardown instructions in
  `infra/README.md` incomplete for anyone deploying this stack.

Which of the two applies was not determined, and is recorded as unresolved
rather than guessed at. The safe operational answer either way is to check
`az keyvault list-deleted` after tearing this stack down, because the failure
it prevents does not appear until the *next* deployment, by which time the
cause is a week behind you.

### Finding 6 — CI had been red since Day 24, and Day 25 is only where it was noticed

Pushing Day 25 turned the `Infra (Bicep)` check red:

```
checking infra/main.dev.bicepparam
ERROR: infra/main.dev.bicepparam(206,50) : Error BCP427: Environment variable
"JWT_SIGNING_KEY" does not exist and there's no default value set.
```

The cause is not in Day 25. `git log -S` puts it in `df9ce84` — Day 24's
Finding 16, which made `apiJwtSigningKey` a `readEnvironmentVariable` with no
default on purpose, so that an unset signing key fails at `build-params`
rather than three minutes into a container restart loop. That reasoning is
right and the parameter should stay as it is.

What it missed is that `.github/workflows/infra.yml` type-checks both
parameter files on every `infra/**` push, and its `env:` block — placeholders
for the SQL admin and the container image — had no such variable. So the
guardrail worked exactly as designed and pointed at CI, which nobody was
watching. The job has been failing on every infra push since that commit.

Reproduced locally with only the four variables the workflow sets, which also
showed the part CI never got to:

```
BEFORE:  FAIL main.dev.bicepparam  (206,50) BCP427
         FAIL main.prod.bicepparam (191,50) BCP427
AFTER:   PASS both
```

Prod fails identically, and CI never reported it because `set -euo pipefail`
stops at the first failure — so fixing only the error the log named would have
produced a second red run for the file it hadn't reached yet.

Fixed by adding `JWT_SIGNING_KEY` to the workflow's existing placeholder
block. The value is not a key and cannot become one: this job runs
`bicep build-params`, which type-checks and never deploys, so nothing is ever
signed with it — the same argument the file already makes for the placeholder
SQL admin object ID.

The transferable point is about what a green pipeline is worth. Days 23 and 24
both describe this job as the thing that stops an invalid template being
discovered three weeks later by whoever was counting on it. It could not have
done that for the last two commits, because it was already failing and the
failure had become the normal state. A check nobody looks at is a check that
has stopped running.

## Files

| File | What changed |
|---|---|
| [`infra/modules/keyvault.bicep`](../../infra/modules/keyvault.bicep) | New. Vault with RBAC authorization, the signing key as a secret, and one `Key Vault Secrets User` assignment scoped to the vault. |
| [`infra/modules/api.bicep`](../../infra/modules/api.bicep) | The container app secret changed from `value: jwtSigningKey` to `keyVaultUrl` + `identity`. The `Jwt__Key` env entry is untouched — only the source of its value moved. |
| [`infra/main.bicep`](../../infra/main.bicep) | Vault wired in after `identity` (needs a principal) and before `api` (which reads its output). Vault name generated differently from every other resource — see Finding 4. New outputs for the vault name, URI and secret URI. |

Verified before deploying: `bicep build main.bicep` exit 0 with no warnings
under the strict `bicepconfig.json`; `bicep lint` clean on all seven modules
and `types.bicep`; `bicep build-params` clean on both parameter files; and the
compiled ARM confirms the container app's `secrets` array contains
`keyVaultUrl` and no `value` property anywhere.

## Source-level sweep

A grep across `QuotesApi/`, `infra/`, `azure.yaml`, `.github/`,
`Quotes.Worker/` and `Quotes.Messaging/` for `password=`, `pwd=`,
`AccountKey=`, `SharedAccessKey=`, `client_secret` and `ClientSecret` returns
nothing. That is a weaker claim than it looks — a sweep proves the absence of
the patterns it searches for, not the absence of secrets — so it is offered
alongside the structural argument rather than instead of it: SQL runs
Entra-ID-only with no administrator login defined, Service Bus runs
`disableLocalAuth: true` so no SAS key is ever issued, and the one remaining
secret is in a vault. The credentials are not hidden; they were never created.

## GitHub link

https://github.com/thinkbridge-thinkschool/Thinkbridge_Shruti_Sahrawat/tree/main/Days/day-25

Commit `953b639`.

## What did you learn this session?

<!-- one line, in your own words -->

## What would break this?

**The vault is a new single point of failure in the startup path.** Before
today, a container app that could reach its own secret store could start.
Now it must additionally reach `kv-dev-....vault.azure.net`, be authenticated,
and be authorised — three more things between a deploy and a running app.
Key Vault has a request-rate limit per vault, and a cold-start stampede across
many replicas resolving references at once is the documented way to meet it.
At this scale (`minReplicas: 0`, `maxReplicas: 2`) it is not close to a
concern; at a scale where it is, the answer is that Container Apps caches a
resolved reference for the life of the revision rather than fetching per
request.

**Rotation is now possible but is not implemented, and those are different
things.** The versionless URI means a new secret version *can* be picked up
without redeploying. Nothing picks it up on a schedule, nothing tests that a
rotated key is read, and — more sharply — nothing handles the window where
tokens signed with the old key are still valid for up to eight hours
(`AccessTokenLifetime`) while the app has moved to the new one. Real rotation
needs the validator to accept both keys during an overlap. Day 25 built the
mechanism rotation would use; it did not build rotation.

**Purge protection is off, and for a production vault that is the wrong
setting.** It is off here because this stack is deployed and destroyed
repeatedly (three times on Day 24 alone) and purge protection would strand the
deterministic vault name for the retention window, breaking exactly the clean
teardown Day 24 exists to demonstrate. A real production vault should have it
on and accept that its names are permanent. The trade is named in
`modules/keyvault.bicep` rather than left as a silent default.

**The secret still passes through ARM.** `jwtSigningKey` is a `@secure()`
parameter, so it is excluded from deployment history — but the stronger
arrangement is a vault created empty and populated out-of-band by
`az keyvault secret set`, where the value never enters a template at all. That
was rejected deliberately: it makes the container app undeployable until a
second manual command has run, and a deployment that half-works by default is
worse than a value passing through a channel that is already treated as
secret. Named as a real trade, not an oversight.

**Nothing proves the app fails correctly if the vault is unreachable.** Every
test above confirms the happy path. What a deny-assignment on the vault, or a
revoked role, or a deleted secret actually produces — a crash loop, a slow
timeout, a 500 on first request — is untested. The failure mode of the
security control is as much a part of it as the control.

