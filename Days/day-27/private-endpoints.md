# Day 27 - the private-endpoint change

## What this adds

A new `enablePrivateEndpoints` parameter (default `false` in `main.bicep`,
turned on for both environments in `main.dev.bicepparam` and
`main.prod.bicepparam`) that builds, for real:

- One VNet (`infra/modules/network.bicep`), `10.20.0.0/16`, with two subnets:
  `private-endpoints` (`10.20.1.0/24`, `privateEndpointNetworkPolicies:
  Disabled`) and `verification` (`10.20.2.0/24`, delegated to
  `Microsoft.ContainerInstance/containerGroups`).
- Three private DNS zones - `privatelink.database.windows.net`,
  `privatelink.vaultcore.azure.net`, and (only when Service Bus is Premium)
  `privatelink.servicebus.windows.net` - each linked to that VNet.
- A private endpoint (`infra/modules/private-endpoint.bicep`, one generic
  module reused three times) for the SQL server, the Key Vault, and - in prod
  only, because Standard does not support this at all - the Service Bus
  namespace.

Every one of these is a real Azure resource on the next `azd provision`, not a
plan. `main.bicep` now outputs the private IP Azure actually assigned each
endpoint (`sqlPrivateEndpointIp`, `keyVaultPrivateEndpointIp`,
`serviceBusPrivateEndpointIp`) so the proof step doesn't have to guess one.

## What this deliberately does not do

It does not turn off public network access on SQL, Key Vault, or Service Bus.

That's not an oversight; it's the one thing this change cannot honestly do.
The API that calls those three services runs in a *borrowed* Container Apps
managed environment (`existingManagedEnvironmentId` in `main.bicep`) that this
stack does not own, that dev and prod currently share, and whose network
configuration was fixed the moment it was created - Azure does not support
adding VNet integration to an existing managed environment. The only way
around that is a second, VNet-injected managed environment, and this
subscription permits exactly one (`Days/day-24`, Finding 7, still true).

So today, switching off public access would not make the data tier private to
the app - it would take the app down, with no route back to its own database.
That fails the one rule that has governed every day of this repo: no loss in
any task. Both paths exist at once instead: private, for anything that can
actually reach this VNet, and public, for the app, until the managed
environment itself can be rebuilt with network injection - a larger, separate
change, named here rather than quietly assumed away.

## How it's proven

`infra/scripts/verify-private-dns.ps1 -Environment dev` (or `prod`):

1. Reads `azd-stack-<env>`'s outputs - the verification subnet ID and the
   private IP Bicep recorded for each endpoint.
2. Starts a one-shot Azure Container Instance (`busybox`, `--restart-policy
   Never`) inside that subnet - the one compute resource in this whole stack
   that *can* sit inside the VNet, since it isn't the shared managed
   environment.
3. Runs `nslookup` from inside the container against the SQL server's,
   the vault's, and (in prod) the Service Bus namespace's ordinary public
   FQDN - the same FQDN the app's own connection string already uses.
4. Compares what came back against the private IP Bicep reported, deletes the
   container group, and fails loudly if any FQDN did not resolve to its
   private address from inside the VNet.

This is the meaningful test, not a weaker one: it isn't asking "does a private
endpoint exist with some IP" (`az network private-endpoint show` would answer
that without proving anything about DNS). It's asking "if something inside
this VNet looked up exactly the hostname the app uses, would it get the
private address" - which is the one thing that can silently be wrong even
after every resource above deploys clean (a zone linked to the wrong VNet, a
missing DNS zone group, a typo in a zone name that Azure will create without
complaint and then never populate).

### The deploy

```
=== azd provision (dev) - this creates real resources ===
  (✓) Done: Resource group: rg-quotes-dev (3.835s)
  (✓) Done: Azure SQL Server: quotes-sql-dev-e6oljhc2krrhe (6.844s)
  (✓) Done: Key Vault: kv-dev-e6oljhc2krrhe (2.197s)
  (✓) Done: Service Bus Namespace: quotes-sb-dev-e6oljhc2krrhe (3.308s)
  (✓) Done: Container App: quotes-api-dev (17.555s)
  (✓) Done: Virtual Network: quotes-vnet-dev (11.209s)
  (✓) Done: Private Endpoint: quotes-sql-dev-e6oljhc2krrhe-pe (32.232s)
  (✓) Done: Private Endpoint: kv-dev-e6oljhc2krrhe-pe (29.829s)

SUCCESS: Your application was provisioned in Azure in 7 minutes 28 seconds.
```

No Service Bus private endpoint, and that is correct rather than missing: dev
runs a Standard namespace, and Standard cannot have one at any setting. The
template made that conditional on the SKU instead of failing or pretending.

### The proof

```
Private endpoints to prove, in rg-quotes-dev:
  SQL server: quotes-sql-dev-e6oljhc2krrhe.database.windows.net -> expect 10.20.1.4 (zone privatelink.database.windows.net)
  Key Vault: kv-dev-e6oljhc2krrhe.vault.azure.net -> expect 10.20.1.5 (zone privatelink.vaultcore.azure.net)

=== SQL server - quotes-sql-dev-e6oljhc2krrhe.database.windows.net ===
  endpoint IP (from the stack): 10.20.1.4
  PASS: quotes-sql-dev-e6oljhc2krrhe.privatelink.database.windows.net A -> 10.20.1.4
  PASS: zone is linked to the private-endpoint VNet.

=== Key Vault - kv-dev-e6oljhc2krrhe.vault.azure.net ===
  endpoint IP (from the stack): 10.20.1.5
  PASS: kv-dev-e6oljhc2krrhe.privatelink.vaultcore.azure.net A -> 10.20.1.5
  PASS: zone is linked to the private-endpoint VNet.

All private endpoints resolve privately inside .../virtualNetworks/quotes-vnet-dev.
```

Re-run after the output fix, against a stack that already existed - which is
the case that failed before, and the one that matters for anything deployed
more than once:

```
=== SQL server - quotes-sql-dev-e6oljhc2krrhe.database.windows.net ===
  endpoint IP (live, from its NIC): 10.20.1.4
  PASS: quotes-sql-dev-e6oljhc2krrhe.privatelink.database.windows.net A -> 10.20.1.4
  PASS: zone is linked to the private-endpoint VNet.

=== Key Vault - kv-dev-e6oljhc2krrhe.vault.azure.net ===
  endpoint IP (live, from its NIC): 10.20.1.5
  PASS: kv-dev-e6oljhc2krrhe.privatelink.vaultcore.azure.net A -> 10.20.1.5
  PASS: zone is linked to the private-endpoint VNet.
```

### Prod, through the pipeline - all three endpoints

Dev can only ever prove two of the three: its Service Bus namespace is
Standard, and Standard does not support private endpoints at any setting.
Prod is Premium, so it is the only environment where the whole data tier can
be proven. It was deployed by merging to main and letting the Day 26
promotion pipeline run it (`deploy-infra` #8, commit 91fde38), not by hand:

```
Private endpoints to prove, in rg-quotes-prod:
  SQL server:  quotes-sql-prod-zcebapajgws7q.database.windows.net -> expect 10.20.1.4
  Key Vault:   kv-prod-zcebapajgws7q.vault.azure.net               -> expect 10.20.1.5
  Service Bus: quotes-sb-prod-zcebapajgws7q.servicebus.windows.net -> expect 10.20.1.6

=== SQL server ===
  endpoint IP (live, from its NIC): 10.20.1.4
  PASS: quotes-sql-prod-zcebapajgws7q.privatelink.database.windows.net A -> 10.20.1.4
  PASS: zone is linked to the private-endpoint VNet.

=== Key Vault ===
  endpoint IP (live, from its NIC): 10.20.1.5
  PASS: kv-prod-zcebapajgws7q.privatelink.vaultcore.azure.net A -> 10.20.1.5
  PASS: zone is linked to the private-endpoint VNet.

=== Service Bus ===
  endpoint IP (live, from its NIC): 10.20.1.6
  PASS: quotes-sb-prod-zcebapajgws7q.privatelink.servicebus.windows.net A -> 10.20.1.6
  PASS: zone is linked to the private-endpoint VNet.

All private endpoints resolve privately inside .../virtualNetworks/quotes-vnet-prod.
```

Six assertions, six passes, and the same template ran unchanged in both
environments - the only difference is the SKU condition that decides whether
the Service Bus endpoint exists at all.

## What went wrong on the way, and what it cost to find

Five failures, none of them in the Bicep, and all five of the same family as
Day 24's: the template was correct and the *environment around it* was not.

1. **`azd provision --preview` is not supported with deployment stacks.**
   `ERROR: preview not supported`. The alpha `deployment.stacks` feature this
   repo turned on for Day 24 has no preview path, so the dry run that would
   normally precede a change like this does not exist here. `az bicep build`
   (syntax + linter) is the only pre-flight left, and it did catch a real
   error before deploying - see 2.

2. **The Bicep linter rejects `privatelink.database.windows.net`.**
   `no-hardcoded-env-urls` is raised to `error` in this repo's
   `bicepconfig.json`, and its default disallowed-host list contains
   `database.windows.net`. A Private Link zone name is a fixed string Azure
   mandates - there is no `environment()`-derived form of it - so the fix is
   the documented one: add it to `excludedhosts` for that rule. Worth noting
   the other two zones were *not* flagged, because `vaultcore.azure.net` and
   `servicebus.windows.net` are not on the default list at all. The rule is
   inconsistent, not wrong.

3. **`az stack sub show` output is flat, not nested under `properties`.**
   The verification script asked for `--query properties.outputs`, got
   nothing back, and reported "no stack named azd-stack-dev" - while
   `az stack sub list` showed that stack sitting there, succeeded. The right
   query is `--query outputs`, which is what this repo's own CI verification
   step already does for `provisioningState`. An empty result and a missing
   resource are not the same thing and should not have shared an error
   message.

4. **`Microsoft.ContainerInstance` was never registered on this
   subscription.** Nothing in this stack had needed ACI before, so the
   provider had never been registered, and the first `az container create`
   failed with `MissingSubscriptionRegistration`. One command
   (`az provider register --namespace Microsoft.ContainerInstance`), but
   exactly the sort of per-subscription precondition a template cannot see -
   the Day 23/24 lesson again.

5. **A VNet-attached container group gets none of az CLI's client-side
   defaults.** Attaching `--subnet` made ARM reject the payload field by
   field: first `InvalidOsType`, then `ResourceRequestsNotSpecified`. The
   same `az container create` without a subnet needs none of `--os-type`,
   `--cpu` or `--memory`. Then the pull itself failed with
   `RegistryErrorResponse` from `index.docker.io` - Docker Hub throttling
   anonymous pulls, which is a coin flip rather than a fix.

6. **An output that passed on the deploy that created the resources, and
   failed on the next deploy of the identical template.** This is the one
   worth keeping. `modules/private-endpoint.bicep` ended with:

   ```bicep
   output privateIp string = endpoint.properties.customDnsConfigs[0].ipAddresses[0]
   ```

   The local deploy that created the endpoints returned that fine. The CI run
   that re-deployed the same template minutes later failed the whole
   deployment with `DeploymentOutputEvaluationFailed: Unable to evaluate
   template outputs: 'privateIp'` - `customDnsConfigs` came back empty that
   time, so `[0]` indexed into nothing. Every resource had already been
   created successfully; the deployment failed on the way out, reporting on
   work that had gone fine.

   Two things follow. A template can be correct and still be
   non-deterministic, if an output reads a field the resource provider does
   not always populate - and "it worked when I ran it" is not evidence of
   the opposite, because the first run is exactly the run most likely to
   have it populated. And the CI verification step built on Day 24's Finding
   12, which exists to stop azd's false failures from failing a good
   deployment, correctly refused to wave this one through: it reported "azd
   failed and the stack is 'failed'. This is a real failure." A check that
   only ever forgives is not a check.

   The fix was not a different field. The template now emits the endpoint
   *names*, which are deterministic, and verify-private-dns.ps1 reads each
   endpoint's current IP off its NIC when it runs - which is better anyway,
   since it compares the DNS record against the address the endpoint has now
   rather than one captured at deploy time.

That last one is why the proof does not depend on a container at all. Three
things have to be true for a name to resolve privately, and every one of them
can be false while every resource still deploys green: the endpoint holds an
IP, the zone holds an A record for that exact hostname pointing at it, and
the zone is linked to the VNet. Azure's resolver serves a linked zone's
records to every client in that VNet by definition, so asserting those three
against Azure's own state proves the same property as `nslookup` does, in
seconds, with nothing to pull and no provider to register. The live lookup is
still there behind `-WithContainerLookup` for anyone who wants the
end-to-end version, and `-ContainerImage` exists because Docker Hub will
throttle it again.

## Cost

A VNet, two subnets, and a private DNS zone are free or near-free by
themselves; a private endpoint is about $0.01/hour plus a small per-GB
processing charge - three of them run for the length of this exercise costs
pennies, nothing like Premium Service Bus's ~₹75/hour. The verification
container runs for under a minute per invocation and is deleted immediately
after, win or fail.
