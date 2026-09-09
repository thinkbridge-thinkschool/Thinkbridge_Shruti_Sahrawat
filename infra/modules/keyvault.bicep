// =============================================================================
// Day 25 - the vault the JWT signing key lives in, and the one role assignment
// that lets the API read it.
//
// Day 24 put the signing key in the container app's own secret store, and said
// so plainly at the time: "a smaller guarantee than Key Vault, and the
// right-sized one for a single signing key with no rotation story yet." This
// module is the day that stops being the right size. What changes is not
// whether the key is masked - it was already masked in `az containerapp show`
// - but where it lives: a container app secret is owned by the app, readable
// by anyone with write access to the app, and versionless. A Key Vault secret
// is a separate resource with its own RBAC, its own audit trail, and versions.
//
// The vault holds exactly one secret. That is not a placeholder for a fuller
// set later: the API authenticates to SQL and Service Bus as a managed
// identity, so there is no database password, no namespace key and no
// connection-string secret to put here. One secret is the whole inventory,
// and the reason it is only one is the subject of Day 25.
// =============================================================================

@description('Vault name. Globally unique, 3-24 characters - shorter than every other name in this stack, which is why main.bicep generates it differently rather than reusing the prefix-kind-environment-token shape that would overflow.')
@minLength(3)
@maxLength(24)
param name string

param location string

param tags object = {}

@description('Principal ID of the user-assigned identity that reads the secret. Empty skips the role assignment, which leaves a vault the API cannot read - useful only for a plan.')
param principalId string = ''

@description('''
The signing key to store. @secure(), so it is excluded from the deployment's
parameter history rather than merely hidden in the portal.

Writing the secret through the template is the weaker of the two honest
options, and it is chosen deliberately. The stronger one is an empty vault
plus `az keyvault secret set` run by an operator, so the value never passes
through ARM at all - but it makes the container app undeployable until a
second, manual, easily-forgotten command has run, and a deployment that half
works is a worse default than a value passing through a channel Microsoft
already treats as secret. The trade is named here rather than left implied.
''')
@secure()
param jwtSigningKey string

@description('Name of the secret inside the vault. Matches the container app secret name in api.bicep, so the two are greppable together.')
param secretName string = 'jwt-key'

@description('''
Purge protection. False here, and that is a real trade rather than an
oversight.

True is correct for a production vault: it prevents a deleted vault being
purged before the retention window expires, which is what stops "delete the
vault" being a way to destroy secrets irrecoverably. It is also irreversible
- once on, it cannot be turned off - and it makes `azd down --purge` unable
to reclaim the name, so the deterministic vault name this stack generates
would be stranded for the full retention period and every later deploy of the
same environment would fail on a name conflict.

This exercise deploys and tears down within the hour (Day 24 did it three
times), so purge protection would make the teardown that Day 24 exists to
demonstrate stop working. A real production stack should set this true and
accept that its vault names are permanent.
''')
param enablePurgeProtection bool = false

@description('Soft-delete retention. 7 is the minimum Azure accepts; the default is 90. Short here for the same reason purge protection is off - a stack that is deployed and destroyed repeatedly wants the shortest window it can have, because the name is unusable until the deleted vault is purged.')
@minValue(7)
@maxValue(90)
param softDeleteRetentionInDays int = 7

// Key Vault Secrets User. Read secret *contents* - not list, not write, not
// manage. The container app needs exactly this and nothing else, which is the
// whole argument for RBAC over the older access-policy model: there is no way
// to express "read this one secret" as an access policy without also granting
// list over everything in the vault.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId

    // RBAC, not access policies. Access policies are per-vault ACLs that live
    // outside Azure's role system entirely - they do not appear in `az role
    // assignment list`, they are not covered by PIM or access reviews, and
    // "who can read this secret" becomes a question you answer by reading a
    // different tool. Every other grant in this stack (SQL, Service Bus,
    // AcrPull) is an RBAC role assignment; this one matches.
    enableRbacAuthorization: true

    enableSoftDelete: true
    softDeleteRetentionInDays: softDeleteRetentionInDays
    enablePurgeProtection: enablePurgeProtection ? true : null

    // No firewall. The container app reaches the vault over the public
    // endpoint, the same way it reaches SQL - and the same caveat from Day 23
    // applies unchanged: the real answer is VNet integration plus a private
    // endpoint, which is a larger change than this exercise covers. Named
    // here rather than left as a comfortable default.
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

resource jwtSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: secretName
  properties: {
    value: jwtSigningKey
    contentType: 'HMAC-SHA256 signing key for QuotesApi access tokens'
  }
}

// Scoped to the vault, not the resource group. A group-scoped grant would let
// this identity read every secret in every vault the group ever gains, which
// is a different and much larger permission than the one being asked for.
resource secretsUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  scope: vault
  name: guid(vault.id, principalId, keyVaultSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

output vaultName string = vault.name
output vaultUri string = vault.properties.vaultUri
output vaultResourceId string = vault.id

// Versionless on purpose. A versioned URI pins the container app to the exact
// secret that existed at deploy time, so rotating the key in the vault would
// change nothing until the next deployment. Versionless means a new version
// is picked up when the app next resolves the reference - which is what makes
// rotation a vault operation rather than a redeploy.
output jwtSecretUri string = '${vault.properties.vaultUri}secrets/${secretName}'
