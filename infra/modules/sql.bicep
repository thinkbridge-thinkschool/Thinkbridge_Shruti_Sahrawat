// =============================================================================
// Azure SQL logical server + one database.
//
// Entra-ID-only authentication, deliberately. `azureADOnlyAuthentication: true`
// means the server has no SQL login at all - not a strong one, not one in Key
// Vault, none. That removes the parameter this template would otherwise have to
// mark @secure(), the value someone would have to pass on the command line, and
// the rotation job nobody writes. The API already authenticates this way (see
// the connection string built in api.bicep: `Authentication=Active Directory
// Managed Identity`), so this is describing what is already true rather than
// tightening anything.
//
// What this module deliberately cannot do: create the *database user* for the
// managed identity. That is `CREATE USER [<identity>] FROM EXTERNAL PROVIDER`
// followed by role membership, and it is T-SQL executed against the database by
// something holding an admin token - there is no ARM resource for it and no way
// to express it in Bicep. It is the one step in this stack that stays a script;
// see infra/README.md.
// =============================================================================

@description('Logical server name. Globally unique across Azure.')
param serverName string

@description('Database name.')
param databaseName string

param location string

param tags object = {}

@description('Display name of the Entra ID admin principal.')
param aadAdminLogin string

@description('Object ID of the Entra ID admin principal.')
param aadAdminObjectId string

@allowed([
  'User'
  'Group'
  'Application'
])
param aadAdminPrincipalType string

import { sqlSku } from '../types.bicep'

@description('Full SKU object - name/tier, plus family and capacity where the tier requires them.')
param databaseSku sqlSku

param maxSizeBytes int

@description('Adds the 0.0.0.0 "allow Azure services" rule. Broader than its name suggests: it admits Azure traffic from any tenant, not only this one.')
param allowAzureServices bool

@description('Backup redundancy. Local is cheapest and is what a dev database should cost; Geo is the default a production database should not have to remember to ask for.')
@allowed([
  'Local'
  'Zone'
  'Geo'
])
param backupStorageRedundancy string = 'Local'

@description('Zone redundancy for the database. Not available on every tier or in every region - southindia rejects it on the tiers used here, which is why both parameter files leave it false.')
param zoneRedundant bool = false

resource server 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: serverName
  location: location
  tags: tags
  properties: {
    // No administratorLogin / administratorLoginPassword: with
    // azureADOnlyAuthentication the pair is not just unnecessary, it is
    // rejected. The absence of those two properties is the security control.
    administrators: {
      administratorType: 'ActiveDirectory'
      login: aadAdminLogin
      sid: aadAdminObjectId
      principalType: aadAdminPrincipalType
      tenantId: tenant().tenantId
      azureADOnlyAuthentication: true
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    version: '12.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: server
  name: databaseName
  location: location
  tags: tags
  sku: databaseSku
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: maxSizeBytes
    requestedBackupStorageRedundancy: backupStorageRedundancy
    zoneRedundant: zoneRedundant
  }
}

// The range 0.0.0.0-0.0.0.0 is a sentinel, not an address: it is how Azure
// spells "allow connections from Azure services". Container Apps has no stable
// outbound IP to allow-list instead, and this stack has no VNet, so the honest
// alternatives are this rule or a private endpoint plus VNet integration.
resource allowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (allowAzureServices) {
  parent: server
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

output serverName string = server.name
output fullyQualifiedDomainName string = server.properties.fullyQualifiedDomainName
output databaseName string = database.name
output serverResourceId string = server.id
