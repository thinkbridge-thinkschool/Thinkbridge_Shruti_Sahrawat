// =============================================================================
// dev - the environment this template is actually run against.
//
// Everything here is chosen to be cheap and disposable: a Basic database, a
// Standard namespace, and an API that scales to zero when nobody is using it.
// Nothing here is a placeholder waiting to be "made real" for production -
// main.prod.bicepparam is that file, and the differences between the two are
// the point of having two.
// =============================================================================

using 'main.bicep'

param environmentName = 'dev'
// Read from the environment, defaulting to the Day 23 value. azd owns
// AZURE_LOCATION for the selected environment, so `azd env set AZURE_LOCATION
// <region>` moves the whole stack without editing this file - which turned out
// to matter: South India refused to provision a *new* Azure SQL server for this
// subscription ("ProvisioningDisabled: Subscriptions are restricted from
// provisioning in this region"), a per-subscription regional capacity
// restriction that Day 23's what-if could not have caught, because what-if
// validates the template and RBAC but never asks the region whether it has
// room. Unset, this still resolves to southindia exactly as Day 23 documented.
param location = readEnvironmentVariable('AZURE_LOCATION', 'southindia')
param resourceGroupName = 'rg-quotes-dev'
param namePrefix = 'quotes'

param tags = {
  costCentre: 'thinkschool'
  owner: 'shruti'
}

// -----------------------------------------------------------------------------
// SQL
// -----------------------------------------------------------------------------

// Read from the environment, not written down. An Entra ID object ID is a
// directory identifier for a real person - it is not a secret, but it is also
// not something that belongs in a repo that gets shared, and reading it at
// deploy time means the file is identical no matter who runs it.
//   $env:SQL_AAD_ADMIN_LOGIN     = az ad signed-in-user show --query userPrincipalName -o tsv
//   $env:SQL_AAD_ADMIN_OBJECT_ID = az ad signed-in-user show --query id -o tsv
param sqlAadAdminLogin = readEnvironmentVariable('SQL_AAD_ADMIN_LOGIN', '')
param sqlAadAdminObjectId = readEnvironmentVariable('SQL_AAD_ADMIN_OBJECT_ID', '')
param sqlAadAdminPrincipalType = 'User'

// Basic: 5 DTUs, 2 GB ceiling, no serverless, no read replicas. It is the
// smallest thing that still runs the Day 7-12 query work honestly.
param sqlDatabaseSku = {
  name: 'Basic'
  tier: 'Basic'
  capacity: 5
}
param sqlDatabaseMaxSizeBytes = 2147483648 // 2 GB - the Basic tier maximum.
param sqlAllowAzureServices = true

// -----------------------------------------------------------------------------
// Service Bus
// -----------------------------------------------------------------------------

// Standard, not Basic: Basic has queues only, so Day 19's whole fan-out design
// is unavailable there, not merely slower.
param serviceBusSku = 'Standard'
param serviceBusTopicName = 'quote-events'

param serviceBusSubscriptions = [
  {
    // Filtered: only QuoteCreated reaches the indexer. A QuoteDeleted is
    // discarded by the broker before delivery - the indexer never receives and
    // ignores it, it never arrives.
    name: 'search-indexer'
    sqlFilter: 'eventType = \'QuoteCreated\''
    maxDeliveryCount: 3
    lockDurationSeconds: 60
  }
  {
    // Catch-all: an audit log that filtered anything would not be an audit log.
    // Empty sqlFilter leaves the $Default TrueFilter in place, which is the
    // correct rule here rather than an omission.
    name: 'audit-log'
    sqlFilter: ''
    maxDeliveryCount: 3
    lockDurationSeconds: 60
  }
]

// -----------------------------------------------------------------------------
// API
// -----------------------------------------------------------------------------

// A public placeholder. A container app cannot be created pointing at an image
// that does not exist, and the real image is built and pushed by a separate
// deploy step - so a first `what-if` of a brand-new environment has to name
// something real. Override on the command line once the real image exists:
//   --parameters apiContainerImage=<registry>/quotes-api:<tag>
param apiContainerImage = readEnvironmentVariable('API_CONTAINER_IMAGE', 'mcr.microsoft.com/k8se/quickstart:latest')
param containerRegistryLoginServer = readEnvironmentVariable('ACR_LOGIN_SERVER', '')
param containerRegistryResourceId = readEnvironmentVariable('ACR_RESOURCE_ID', '')

// Scale to zero. The trade is a cold start on the first request after idle, and
// an empty in-process HybridCache L1 when the replica comes back - acceptable in
// dev, and the reason prod does not do it.
param apiMinReplicas = 0
param apiMaxReplicas = 2
param apiCpu = '0.5'
param apiMemory = '1Gi'
param apiConcurrentRequests = 50

// Migrate, not EnsureCreated. This database is created by this template, so it
// starts empty and every migration - including AddOutbox - applies cleanly.
// The existing azd-created database cannot switch to this: it was bootstrapped
// with EnsureCreated() and has no __EFMigrationsHistory table, so the first
// migration would try to create tables that are already there. See
// infra/README.md.
param apiSchemaBootstrap = 'Migrate'
param apiAspNetCoreEnvironment = 'Production'

param logAnalyticsRetentionInDays = 30
