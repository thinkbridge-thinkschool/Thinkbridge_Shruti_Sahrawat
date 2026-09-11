// =============================================================================
// Day 27 - one private endpoint, wired into its matching private DNS zone.
//
// Generic on purpose. SQL, Key Vault and Service Bus each need exactly this
// shape: a NIC in a subnet, one privateLinkServiceConnection naming the target
// resource and its groupId, and a DNS zone group that keeps the zone's record
// pointed at whatever IP the endpoint actually gets - including after the
// endpoint is deleted and re-created with a different address. Three
// near-identical copies of this file, one per service, would be three places
// to fix the same bug; this is the one place instead.
// =============================================================================

@description('Name of the private endpoint resource.')
param name string

param location string

param tags object = {}

@description('Subnet the endpoint NIC lands in. Must have privateEndpointNetworkPolicies set to Disabled.')
param subnetId string

@description('Resource ID of the PaaS resource being reached privately - the SQL server, the vault, or the Service Bus namespace.')
param targetResourceId string

@description('Which sub-resource of the target this endpoint connects to. Fixed per service by Azure, not a free choice: sqlServer for Microsoft.Sql/servers, vault for Microsoft.KeyVault/vaults, namespace for Microsoft.ServiceBus/namespaces.')
param groupId string

@description('Private DNS zone this endpoint auto-registers an A record into, e.g. the resource ID of a privatelink.database.windows.net zone.')
param privateDnsZoneId string

resource endpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    subnet: {
      id: subnetId
    }
    privateLinkServiceConnections: [
      {
        name: name
        properties: {
          privateLinkServiceId: targetResourceId
          groupIds: [
            groupId
          ]
        }
      }
    ]
  }
}

// Without this, the endpoint gets an IP but nothing ever tells the private DNS
// zone about it - the zone stays empty and every lookup keeps returning the
// public address, which looks identical to "the endpoint doesn't work" from
// the outside. This is what makes the A record appear, and keeps it correct if
// the endpoint is ever deleted and recreated with a new IP.
resource dnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = {
  parent: endpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: groupId
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}

output id string = endpoint.id
output name string = endpoint.name

@description('The private IP Azure actually assigned, read from the endpoint resource itself rather than assumed from the subnet prefix - Azure hands out the first free address in the subnet, which is not necessarily the fourth one.')
output privateIp string = endpoint.properties.customDnsConfigs[0].ipAddresses[0]
