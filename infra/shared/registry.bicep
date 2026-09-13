@description('Globally unique registry name.')
param name string

param location string
param sku string
param tags object = {}

resource registry 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: sku
  }
  properties: {
    // Off, and it stays off. Enabling the admin user creates a username and
    // password pair that then has to live somewhere - a pipeline secret, a
    // container app secret, someone's notes - which is precisely the class of
    // credential Day 25 removed from this project everywhere else. The API
    // pulls with its user-assigned managed identity via
    // modules/registry-access.bicep's AcrPull assignment, and CI pushes with
    // the federated GitHub identity. Neither needs a password.
    adminUserEnabled: false

    // Enabled, matching what is deployed. A private endpoint on the registry
    // would need the Premium SKU, and would also have to be reachable from the
    // Container Apps environment - which is not VNet-integrated, for the same
    // reason SQL still accepts public traffic (Days/day-27). Turning this off
    // without solving that would stop the API pulling images at all.
    publicNetworkAccess: 'Enabled'
    anonymousPullEnabled: false
  }
}

output name string = registry.name
output loginServer string = registry.properties.loginServer
output resourceId string = registry.id
