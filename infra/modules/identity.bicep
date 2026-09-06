// =============================================================================
// The one identity the whole stack authenticates with.
//
// A user-assigned identity rather than a system-assigned one, for a reason that
// only shows up later: a system-assigned identity is created with its container
// app and destroyed with it, so its object ID changes every time the app is
// recreated - and every SQL user and every role assignment that referenced the
// old ID silently stops matching anything. A user-assigned identity outlives the
// compute that uses it, which is what lets `CREATE USER ... FROM EXTERNAL
// PROVIDER` in the database stay correct across a redeploy.
//
// It is also why this is its own module rather than a resource inside api.bicep:
// SQL and Service Bus both need the principal ID, and the API needs the client
// ID for its connection string. Whoever creates it has to come first.
// =============================================================================

@description('Name of the user-assigned managed identity.')
param name string

param location string

param tags object = {}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: name
  location: location
  tags: tags
}

@description('Resource ID - what a container app references to attach the identity.')
output resourceId string = identity.id

@description('Principal (object) ID - what a role assignment grants to, and what SQL matches an external user against.')
output principalId string = identity.properties.principalId

@description('Client ID - what goes in the SQL connection string as User Id, so the driver knows which identity to request a token for when more than one is attached.')
output clientId string = identity.properties.clientId

output name string = identity.name
