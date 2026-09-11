// =============================================================================
// Day 27 - the VNet the data tier's private endpoints live in.
//
// Two subnets, not one. A private endpoint's NIC only has its network policies
// enforcement skipped when its subnet sets `privateEndpointNetworkPolicies:
// 'Disabled'` - that is a property of the subnet, not the endpoint, so the
// three endpoints created against this VNet all share one subnet with that
// setting on. The verification subnet has no reason to carry it and every
// reason not to: it is delegated to Microsoft.ContainerInstance/containerGroups
// instead, and a subnet cannot both be delegated to container groups and host
// a private endpoint NIC.
//
// What this VNet is not: a place the API lives. The container app that reaches
// SQL, Key Vault and Service Bus runs in a *borrowed* Container Apps managed
// environment (see existingManagedEnvironmentId in main.bicep) that this stack
// does not own, that is shared between dev and prod, and whose network
// configuration (none) was fixed the moment it was created - Azure does not
// allow adding VNet integration to an existing managed environment. Recreating
// it with VNet injection would need a second managed environment, and this
// subscription permits exactly one (Days/day-24, Finding 7). So no template run
// from this repo can join the app to this VNet. That is why main.bicep builds
// this private path and proves it resolves without also switching off the data
// tier's public endpoint - see Days/day-27/private-endpoints.md for the full
// reasoning.
// =============================================================================

@description('Name of the VNet.')
param vnetName string

param location string

param tags object = {}

@description('Address space for the whole VNet, e.g. 10.20.0.0/16.')
param addressPrefix string

@description('Subnet the three private endpoints\' NICs land in.')
param privateEndpointSubnetPrefix string

@description('Subnet for the short-lived container instance that proves DNS resolution from inside the VNet. Delegated to Microsoft.ContainerInstance/containerGroups.')
param verificationSubnetPrefix string

@description('Create the Service Bus private DNS zone and link it to this VNet. Only meaningful when a Service Bus private endpoint will actually exist - Standard namespaces do not support private endpoints at all, so a Standard-only run (dev, today) has no use for this zone and should not pay even the small monthly cost of one.')
param includeServiceBusZone bool

var sqlZoneName = 'privatelink.database.windows.net'
var vaultZoneName = 'privatelink.vaultcore.azure.net'
var serviceBusZoneName = 'privatelink.servicebus.windows.net'

resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: vnetName
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        addressPrefix
      ]
    }
    subnets: [
      {
        name: 'private-endpoints'
        properties: {
          addressPrefix: privateEndpointSubnetPrefix
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
      {
        name: 'verification'
        properties: {
          addressPrefix: verificationSubnetPrefix
          delegations: [
            {
              name: 'aci-delegation'
              properties: {
                serviceName: 'Microsoft.ContainerInstance/containerGroups'
              }
            }
          ]
        }
      }
    ]
  }
}

// Private DNS zones are not regional resources - Azure requires 'global' here
// regardless of where the VNet or the zoned resource lives. Not a stand-in for
// a location parameter; every zone Azure Private Link uses is created this way.
resource sqlZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: sqlZoneName
  location: 'global'
  tags: tags
}

resource vaultZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: vaultZoneName
  location: 'global'
  tags: tags
}

resource serviceBusZone 'Microsoft.Network/privateDnsZones@2024-06-01' = if (includeServiceBusZone) {
  name: serviceBusZoneName
  location: 'global'
  tags: tags
}

resource sqlZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: sqlZone
  name: '${vnetName}-link'
  location: 'global'
  properties: {
    virtualNetwork: {
      id: vnet.id
    }
    // Registration would let every VM on this VNet auto-add its own A record
    // to the zone. Nothing on this VNet is a VM with a workload of its own -
    // it exists to host private endpoints and one throwaway verification
    // container - so there is nothing that should be registering itself here.
    registrationEnabled: false
  }
}

resource vaultZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: vaultZone
  name: '${vnetName}-link'
  location: 'global'
  properties: {
    virtualNetwork: {
      id: vnet.id
    }
    registrationEnabled: false
  }
}

resource serviceBusZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = if (includeServiceBusZone) {
  parent: serviceBusZone
  name: '${vnetName}-link'
  location: 'global'
  properties: {
    virtualNetwork: {
      id: vnet.id
    }
    registrationEnabled: false
  }
}

output vnetId string = vnet.id
output privateEndpointSubnetId string = vnet.properties.subnets[0].id
output verificationSubnetId string = vnet.properties.subnets[1].id
output sqlZoneId string = sqlZone.id
output vaultZoneId string = vaultZone.id
output serviceBusZoneId string = includeServiceBusZone ? serviceBusZone.id : ''
