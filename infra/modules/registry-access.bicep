// =============================================================================
// AcrPull for this stack's identity, on a registry the stack does not own.
//
// Deployed at the registry's own resource group scope from main.bicep. Without
// it, the container app is created pointing at an image it cannot pull and
// reports a perfectly clear ImagePullBackOff hours after the deployment reported
// success - the sort of gap that usually gets closed with one `az role
// assignment create` typed once, forgotten, and missing the next time the
// environment is built from scratch. Which is the exact failure "no portal
// click-ops" is meant to rule out.
// =============================================================================

@description('Name of the existing container registry.')
param registryName string

@description('Principal to grant AcrPull to.')
param principalId string

// Built-in AcrPull role. Verify with `az role definition list --name AcrPull`.
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, principalId, acrPullRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

output roleAssignmentId string = acrPull.id
