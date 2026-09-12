// =============================================================================
// Day 23 - the Quotes stack as code.
//
// Subscription-scoped on purpose. A resource-group-scoped template can only
// deploy *into* a group somebody already made, which leaves the first and most
// consequential resource in the stack - the group itself, its name, its region,
// its tags - as the one thing still created by hand in the portal. Deploying at
// subscription scope means `what-if` can be run against a name that does not
// exist yet and still print a complete plan, which is exactly the property that
// makes a review of a *new* environment possible before it costs anything.
//
// Nothing in this file is secret. The API reaches SQL and Service Bus with a
// user-assigned managed identity, so there is no password, no connection-string
// key and no `@secure()` parameter anywhere in the stack - see
// modules/identity.bicep for why that identity is created first and passed
// around rather than being minted inside whichever module happens to need it.
// =============================================================================

targetScope = 'subscription'

import { topicSubscription, sqlSku } from 'types.bicep'

// -----------------------------------------------------------------------------
// Shape of the environment
// -----------------------------------------------------------------------------

@description('Which parameter file is driving this deployment. Only used for tagging and naming - every behavioural difference between dev and prod is an explicit parameter below, never an `if (environmentName == ...)` branch buried in a module.')
@allowed([
  'dev'
  'prod'
])
param environmentName string

@description('Azure region for every resource in the stack. southindia is where the Day 5 container app already lives; Static Web Apps (Day 17) is deliberately not in this template - it has its own deploy path and its own short list of supported regions.')
param location string

@description('Resource group to create and deploy into. Named explicitly rather than derived, because the name of the thing that holds everything else is not a detail to leave to a naming function.')
param resourceGroupName string

@description('Prefix for generated resource names.')
@minLength(3)
@maxLength(10)
param namePrefix string = 'quotes'

@description('Tags applied to the resource group and inherited by every module.')
param tags object = {}

// A short, deterministic suffix so globally-unique names (SQL server, Service
// Bus namespace) do not collide with someone else's in the same region. Derived
// from the subscription and group name, so re-running this template for the same
// environment produces the same names - a random suffix would make every
// deployment look like a brand-new stack to `what-if`.
var resourceToken = toLower(uniqueString(subscription().id, resourceGroupName, environmentName))

var defaultTags = union(tags, {
  environment: environmentName
  workload: namePrefix
  managedBy: 'bicep'
})

// Resolved names. Explicit parameter wins; otherwise the generated name. This
// indirection is what lets the same template either stand up a fresh
// environment or be pointed at resources that already exist under names nobody
// chose with a naming convention in mind.
var resolvedSqlServerName = empty(sqlServerName) ? '${namePrefix}-sql-${environmentName}-${resourceToken}' : sqlServerName
var resolvedServiceBusNamespaceName = empty(serviceBusNamespaceName) ? '${namePrefix}-sb-${environmentName}-${resourceToken}' : serviceBusNamespaceName

// Key Vault names cap at 24 characters, where SQL and Service Bus allow far
// more - so this one cannot reuse the `${prefix}-${kind}-${env}-${token}`
// shape the other two share. `quotes-kv-prod-<13-char token>` is 27 and would
// fail at deploy time on a length rule, not at build. Dropping the prefix
// keeps it at 20-21 and still globally unique, since the token is what
// provides uniqueness in every one of these names anyway.
var resolvedKeyVaultName = empty(keyVaultName) ? 'kv-${environmentName}-${resourceToken}' : keyVaultName

// The container app's region: the stack's, unless apiLocation says otherwise.
//
// This used to be honoured *only* when a borrowed environment pinned the app
// somewhere else, and ignored whenever the stack created its own environment.
// The reasoning was sound - the api module uses one `location` for the app,
// the managed environment and the workspace, so honouring apiLocation on the
// owned path moves all three, and a value left over from a previous borrowed
// run could relocate a whole environment without anyone asking for it.
//
// It stopped being the right trade the first time a subscription refused to
// host a managed environment in the stack's region at all. Azure for Students
// answers `MaxNumberOfEnvironmentsInSubExceeded` for Central India with zero
// environments in existence, and its region policy allows only centralindia,
// eastasia, koreacentral, indiasouthcentral and uaenorth - so the data tier
// must stay in Central India (SQL, Key Vault and Service Bus all deploy there
// happily) while the app and its environment go to UAE North. With the old
// guard there was no way to express that: the only lever was `location`, and
// moving that moves the database too.
//
// So apiLocation now means what its description says in both cases, and on
// the owned path it moves the app, its managed environment and its Log
// Analytics workspace together - which is the only coherent thing it could
// mean, since a container app must live in its environment's region. The data
// tier stays at `location`. That cross-region hop is real and is the same one
// Days 23-24 measured, arrived at from the opposite direction: there the
// environment was fixed and the database had to move, here the database is
// fixed and the environment has to.
var resolvedApiLocation = empty(apiLocation) ? location : apiLocation

// -----------------------------------------------------------------------------
// SQL
// -----------------------------------------------------------------------------

@description('Azure SQL logical server name. Globally unique. Left empty, it is generated from the prefix, the environment and a deterministic token - a parameter default cannot reference a variable, which is why the fallback lives in a var below rather than here.')
param sqlServerName string = ''

@description('Database name. Kept identical across environments so a connection string differs only by server, never by database.')
param sqlDatabaseName string = 'quotesdb'

@description('Display name of the Entra ID principal that administers the server. This is a human (or group) name, not a credential.')
param sqlAadAdminLogin string

@description('Object ID of that Entra ID principal. Supplied from the environment at deploy time (see the .bicepparam files) so no directory object ID is committed to the repo.')
@minLength(36)
@maxLength(36)
param sqlAadAdminObjectId string

@description('Whether the admin principal is a User, Group or Application. A group is the better answer for anything shared; User is the honest answer for a one-person exercise.')
@allowed([
  'User'
  'Group'
  'Application'
])
param sqlAadAdminPrincipalType string = 'User'

@description('Database SKU. A full object rather than a name, because Basic takes no `family` and General Purpose requires one - flattening this to a string would need a lookup table that lies the first time a tier is added.')
param sqlDatabaseSku sqlSku

@description('Max database size in bytes.')
param sqlDatabaseMaxSizeBytes int

@description('Allow other Azure services (including Container Apps) to reach the server. This is the 0.0.0.0 firewall rule, which is broader than it looks - it admits every Azure tenant, not just this one. It is on here because the container app has no fixed outbound IP and no private endpoint in this stack; the note in the write-up says what replaces it.')
param sqlAllowAzureServices bool = true

// -----------------------------------------------------------------------------
// Service Bus
// -----------------------------------------------------------------------------

@description('Service Bus namespace name. Globally unique. Generated when left empty, same as the SQL server name.')
param serviceBusNamespaceName string = ''

@description('Namespace SKU. Topics do not exist on Basic at all, so Standard is the floor for Day 19 to work.')
@allowed([
  'Standard'
  'Premium'
])
param serviceBusSku string

@description('Messaging units. Premium only; ignored on Standard.')
param serviceBusCapacity int = 1

@description('Topic name. Matches ServiceBus:TopicName in Quotes.Worker/appsettings.json.')
param serviceBusTopicName string = 'quote-events'

@description('Subscriptions on the topic. `sqlFilter` empty means "take everything" - see modules/servicebus.bicep for why an empty filter is not the same as no rule.')
param serviceBusSubscriptions topicSubscription[]

// -----------------------------------------------------------------------------
// API (Container App)
// -----------------------------------------------------------------------------

@description('Container app name.')
param apiName string = '${namePrefix}-api'

@description('Image the API runs. Dev defaults to a public placeholder because a container app cannot be created pointing at an image that does not exist yet, and the real image is pushed by a separate deploy step, not by this template.')
param apiContainerImage string

@description('Login server of the registry holding the image, e.g. myregistry.azurecr.io. Empty for a public image.')
param containerRegistryLoginServer string = ''

@description('Resource ID of that registry, when it lives outside this stack. Supplying it grants this stack\'s identity AcrPull on it, so pulling the image needs no admin user, no password and no portal click.')
param containerRegistryResourceId string = ''

@description('Floor on replicas. 0 lets dev scale to nothing and cost nothing; anything above 1 means the Day 21 HybridCache L1 is per-replica, which is a real behavioural difference and not just a cost knob.')
@minValue(0)
param apiMinReplicas int

@minValue(1)
param apiMaxReplicas int

@description('vCPU per replica, as a string because Bicep has no decimal type - `json()` converts it in the module.')
param apiCpu string

@description('Memory per replica. Container Apps requires a fixed cpu:memory ratio; 0.5/1Gi and 1.0/2Gi are both valid pairs.')
param apiMemory string

@description('Concurrent requests per replica before another replica is added.')
param apiConcurrentRequests int = 50

@description('How the API creates its schema on startup. Read by Program.cs as Database:SchemaBootstrap and validated there - a typo throws rather than silently picking a default.')
@allowed([
  'Migrate'
  'EnsureCreated'
])
param apiSchemaBootstrap string

@description('ASPNETCORE_ENVIRONMENT for the container.')
param apiAspNetCoreEnvironment string = 'Production'

@description('''
HMAC-SHA256 JWT signing key. Required in every environment this template
deploys, because apiAspNetCoreEnvironment defaults to Production and
QuotesApi refuses to start in Production without it - discovered by running
this template for real, not by reading the app's code first. See
modules/api.bicep for the full reasoning and Days/day-24 for the crash-loop
that found it.
''')
@secure()
param apiJwtSigningKey string

@description('Key Vault name. Globally unique, 3-24 characters. Generated from the environment and the deterministic token when left empty.')
@maxLength(24)
param keyVaultName string = ''

@description('Purge protection on the vault. False by default - see modules/keyvault.bicep for why a stack torn down as often as this one cannot have it on.')
param keyVaultEnablePurgeProtection bool = false

@description('Log Analytics retention. The workspace is part of the API module because a container apps environment cannot exist without one. Ignored when existingManagedEnvironmentId is set, because no workspace is created then either.')
@minValue(30)
@maxValue(730)
param logAnalyticsRetentionInDays int

@description('''
Resource ID of a Container Apps managed environment that already exists, to
host the API in rather than creating one.

Left empty, this template behaves exactly as Day 23 - the environment and its
workspace are part of the stack. Set, the container app joins an environment
this stack does not manage. The reason it exists: this subscription permits one
managed environment, that one runs the live quotes-api, and a second cannot be
created at any price - see Days/day-24, Finding 4. Full reasoning in
modules/api.bicep.
''')
param existingManagedEnvironmentId string = ''

@description('''
Region for the container app, when it differs from the stack's. Honoured on
both paths: with a borrowed environment it names the region that environment
already sits in, and when this stack creates its own it moves the app, the
managed environment and the Log Analytics workspace there together. The data
tier always stays at `location`.

It exists because a container app must sit in its environment's region, and a
borrowed environment's region is not this stack's to choose: the existing
environment is in southindia, and southindia will not provision a new Azure
SQL server for this subscription (`ProvisioningDisabled`, Finding 2). So the
stack deploys to centralindia and the app alone sits in southindia beside the
environment it joins. That is a real cross-region hop from app to database and
a real latency cost, named here rather than left to be found on a p99 chart.
''')
param apiLocation string = ''

// -----------------------------------------------------------------------------
// Private endpoints (Day 27)
// -----------------------------------------------------------------------------

@description('''
Builds a VNet, private DNS zones, and private endpoints for SQL, Key Vault and
- on Premium - Service Bus, and lets infra/scripts/verify-private-dns.ps1 prove
DNS resolves them privately from inside that VNet. See
Days/day-27/private-endpoints.md for the full write-up.

Deliberately does not also disable public network access on those resources.
The API that calls them cannot join this VNet: it runs in a borrowed Container
Apps managed environment (see existingManagedEnvironmentId above) that this
stack does not own and that cannot have VNet integration added after the fact,
and this subscription cannot create a second, VNet-integrated environment
either (Days/day-24, Finding 7). Turning off public access today would not
make the data tier private to the app - it would make it unreachable by the
app. So both paths exist at once: private, for anything that can reach this
VNet, and public, for the app, until the environment itself can be rebuilt
with network injection.
''')
param enablePrivateEndpoints bool = false

@description('Address space for the private-endpoint VNet. Chosen only to be obviously distinct from anything else in this exercise - there is no VNet peering anywhere in this stack, so the one real requirement is internal consistency.')
param vnetAddressPrefix string = '10.20.0.0/16'

@description('Subnet the three private endpoints\' NICs land in.')
param privateEndpointSubnetPrefix string = '10.20.1.0/24'

@description('Subnet for the short-lived container instance that proves DNS resolution. Delegated to Microsoft.ContainerInstance/containerGroups, which is incompatible with also hosting a private endpoint - hence a second subnet rather than one.')
param verificationSubnetPrefix string = '10.20.2.0/24'

// =============================================================================
// Resource group
// =============================================================================

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: defaultTags
}

// =============================================================================
// Modules, in dependency order
//
// identity first, because both data-plane modules need a principal to grant to
// and the API needs a client ID to put in its connection string. Creating the
// identity inside the API module instead would force SQL and Service Bus to
// depend on the API, which is backwards: the API is the thing that depends on
// them.
// =============================================================================

module identity 'modules/identity.bicep' = {
  scope: rg
  name: 'identity'
  params: {
    name: '${namePrefix}-id-${environmentName}'
    location: location
    tags: defaultTags
  }
}

module sql 'modules/sql.bicep' = {
  scope: rg
  name: 'sql'
  params: {
    serverName: resolvedSqlServerName
    databaseName: sqlDatabaseName
    location: location
    tags: defaultTags
    aadAdminLogin: sqlAadAdminLogin
    aadAdminObjectId: sqlAadAdminObjectId
    aadAdminPrincipalType: sqlAadAdminPrincipalType
    databaseSku: sqlDatabaseSku
    maxSizeBytes: sqlDatabaseMaxSizeBytes
    allowAzureServices: sqlAllowAzureServices
  }
}

module serviceBus 'modules/servicebus.bicep' = {
  scope: rg
  name: 'servicebus'
  params: {
    namespaceName: resolvedServiceBusNamespaceName
    location: location
    tags: defaultTags
    skuName: serviceBusSku
    capacity: serviceBusCapacity
    topicName: serviceBusTopicName
    subscriptions: serviceBusSubscriptions
    principalId: identity.outputs.principalId
  }
}

// The vault, and the one role assignment that lets the API read out of it.
// Placed after identity because the role assignment needs a principal, and
// before api because the container app resolves its Key Vault reference at
// create time - see the dependsOn on the api module below.
module keyVault 'modules/keyvault.bicep' = {
  scope: rg
  name: 'keyvault'
  params: {
    name: resolvedKeyVaultName
    location: location
    tags: defaultTags
    principalId: identity.outputs.principalId
    jwtSigningKey: apiJwtSigningKey
    enablePurgeProtection: keyVaultEnablePurgeProtection
  }
}

// Only when the image comes from a private registry this stack does not own.
// Scoped to the registry's own resource group, which is the whole reason main
// is subscription-scoped: a group-scoped template cannot grant a role on a
// resource that lives somewhere else without a second, manual deployment.
module registryAccess 'modules/registry-access.bicep' = if (!empty(containerRegistryResourceId)) {
  scope: resourceGroup(split(containerRegistryResourceId, '/')[4])
  name: 'registry-access'
  params: {
    registryName: last(split(containerRegistryResourceId, '/'))
    principalId: identity.outputs.principalId
  }
}

module api 'modules/api.bicep' = {
  scope: rg
  name: 'api'
  params: {
    name: apiName
    location: resolvedApiLocation
    tags: defaultTags
    environmentName: '${namePrefix}-env-${environmentName}'
    existingManagedEnvironmentId: existingManagedEnvironmentId
    logAnalyticsName: '${namePrefix}-logs-${environmentName}'
    logAnalyticsRetentionInDays: logAnalyticsRetentionInDays
    identityResourceId: identity.outputs.resourceId
    identityClientId: identity.outputs.clientId
    containerImage: apiContainerImage
    containerRegistryLoginServer: containerRegistryLoginServer
    minReplicas: apiMinReplicas
    maxReplicas: apiMaxReplicas
    cpu: apiCpu
    memory: apiMemory
    concurrentRequests: apiConcurrentRequests
    sqlServerFqdn: sql.outputs.fullyQualifiedDomainName
    sqlDatabaseName: sql.outputs.databaseName
    serviceBusFqdn: serviceBus.outputs.fullyQualifiedNamespace
    schemaBootstrap: apiSchemaBootstrap
    aspNetCoreEnvironment: apiAspNetCoreEnvironment
    jwtSecretUri: keyVault.outputs.jwtSecretUri
    keyVaultIdentityResourceId: identity.outputs.resourceId
  }
  dependsOn: [
    registryAccess
    // No explicit keyVault entry here, and that is worth stating because the
    // first draft had one. The container app resolves its Key Vault reference
    // at create time, so it needs the *role assignment* inside the keyvault
    // module to exist first - and the instinct was to depend on the module to
    // guarantee that. The strict linter rejected it as redundant
    // (no-unnecessary-dependson), and the linter is right: reading
    // keyVault.outputs.jwtSecretUri below already depends on the whole nested
    // deployment, which does not report outputs until every resource in it -
    // vault, secret and role assignment - has finished. Adding the dependency
    // by hand expressed a guarantee the reference had already made.
    //
    // What neither expresses is RBAC *propagation*, which is eventual and can
    // outrun any ordering ARM understands; see Days/day-25.
  ]
}

// Private endpoints for the data tier. All conditional on enablePrivateEndpoints
// so a plain `azd provision` with the default keeps behaving exactly as every
// earlier day recorded it - this is additive, not a replacement for the public
// path described in modules/sql.bicep, modules/keyvault.bicep and
// modules/servicebus.bicep.
module network 'modules/network.bicep' = if (enablePrivateEndpoints) {
  scope: rg
  name: 'network'
  params: {
    vnetName: '${namePrefix}-vnet-${environmentName}'
    location: location
    tags: defaultTags
    addressPrefix: vnetAddressPrefix
    privateEndpointSubnetPrefix: privateEndpointSubnetPrefix
    verificationSubnetPrefix: verificationSubnetPrefix
    includeServiceBusZone: serviceBusSku == 'Premium'
  }
}

module sqlPrivateEndpoint 'modules/private-endpoint.bicep' = if (enablePrivateEndpoints) {
  scope: rg
  name: 'sql-private-endpoint'
  params: {
    name: '${resolvedSqlServerName}-pe'
    location: location
    tags: defaultTags
    subnetId: network!.outputs.privateEndpointSubnetId
    targetResourceId: sql.outputs.serverResourceId
    groupId: 'sqlServer'
    privateDnsZoneId: network!.outputs.sqlZoneId
  }
}

module keyVaultPrivateEndpoint 'modules/private-endpoint.bicep' = if (enablePrivateEndpoints) {
  scope: rg
  name: 'keyvault-private-endpoint'
  params: {
    name: '${resolvedKeyVaultName}-pe'
    location: location
    tags: defaultTags
    subnetId: network!.outputs.privateEndpointSubnetId
    targetResourceId: keyVault.outputs.vaultResourceId
    groupId: 'vault'
    privateDnsZoneId: network!.outputs.vaultZoneId
  }
}

// Service Bus private endpoints require Premium - Standard does not support
// them at all, so this only ever creates anything in prod, and only when prod
// is actually on Premium (see serviceBusSku in the .bicepparam files).
module serviceBusPrivateEndpoint 'modules/private-endpoint.bicep' = if (enablePrivateEndpoints && serviceBusSku == 'Premium') {
  scope: rg
  name: 'servicebus-private-endpoint'
  params: {
    name: '${resolvedServiceBusNamespaceName}-pe'
    location: location
    tags: defaultTags
    subnetId: network!.outputs.privateEndpointSubnetId
    targetResourceId: serviceBus.outputs.namespaceResourceId
    groupId: 'namespace'
    privateDnsZoneId: network!.outputs.serviceBusZoneId
  }
}

// =============================================================================
// Outputs - the values the next step needs, so nobody has to go and read them
// out of the portal.
// =============================================================================

output resourceGroupName string = rg.name
output apiFqdn string = api.outputs.fqdn
output apiHealthUrl string = 'https://${api.outputs.fqdn}/health'
output apiResourceId string = api.outputs.resourceId
output sqlServerFqdn string = sql.outputs.fullyQualifiedDomainName
output sqlDatabaseName string = sql.outputs.databaseName
output serviceBusFqdn string = serviceBus.outputs.fullyQualifiedNamespace
output serviceBusTopic string = serviceBus.outputs.topicName
output managedIdentityClientId string = identity.outputs.clientId
output managedIdentityPrincipalId string = identity.outputs.principalId
output managedIdentityName string = identity.outputs.name
output keyVaultName string = keyVault.outputs.vaultName
output keyVaultUri string = keyVault.outputs.vaultUri
output jwtSecretUri string = keyVault.outputs.jwtSecretUri

@description('The environment the API actually runs in, whether this stack created it or borrowed an existing one.')
output apiManagedEnvironmentId string = api.outputs.managedEnvironmentId

@description('False when the environment was borrowed - which also means `azd down` will not take it with it, and that is the intended behaviour, not a gap.')
output apiOwnsManagedEnvironment bool = api.outputs.ownsManagedEnvironment

@description('Region the container app landed in. Equal to the stack location unless a borrowed environment pinned it elsewhere - which is the only case where these two differ, and worth reading back rather than inferring.')
output apiDeployedLocation string = resolvedApiLocation

// -----------------------------------------------------------------------------
// Private endpoints (Day 27) - empty strings when enablePrivateEndpoints is
// false, rather than omitted, so infra/scripts/verify-private-dns.ps1 can tell
// "not built this run" apart from "output does not exist yet".
// -----------------------------------------------------------------------------

output privateEndpointsEnabled bool = enablePrivateEndpoints

@description('Resource ID of the private-endpoint VNet, when built.')
output privateEndpointVnetId string = enablePrivateEndpoints ? network!.outputs.vnetId : ''

@description('Subnet the verification container instance runs in - see infra/scripts/verify-private-dns.ps1.')
output privateEndpointVerificationSubnetId string = enablePrivateEndpoints ? network!.outputs.verificationSubnetId : ''

@description('Name of the SQL server\'s private endpoint. A name rather than the IP it was given: an output reading customDnsConfigs[0] failed a later deployment of this same template with DeploymentOutputEvaluationFailed when that array came back empty (see modules/private-endpoint.bicep). infra/scripts/verify-private-dns.ps1 resolves the current IP from the endpoint\'s NIC instead.')
output sqlPrivateEndpointName string = enablePrivateEndpoints ? sqlPrivateEndpoint!.outputs.name : ''

@description('Name of the Key Vault\'s private endpoint.')
output keyVaultPrivateEndpointName string = enablePrivateEndpoints ? keyVaultPrivateEndpoint!.outputs.name : ''

@description('Name of the Service Bus namespace\'s private endpoint. Empty whenever the namespace is Standard, not just when private endpoints are disabled - Standard cannot have one at any setting.')
output serviceBusPrivateEndpointName string = (enablePrivateEndpoints && serviceBusSku == 'Premium') ? serviceBusPrivateEndpoint!.outputs.name : ''

