// =============================================================================
// prod - never deployed, and labelled as such rather than implied.
//
// This file has not been run against a real subscription: the exercise's budget
// is one environment, and standing up a Premium Service Bus namespace to prove a
// parameter file parses would be an expensive way to learn something `bicep
// build-params` already tells you. What it is verified to be is *valid* - it
// compiles, and its every value is type-checked against main.bicep.
//
// Each difference from dev below is a decision with a reason, not a bigger
// number for its own sake.
// =============================================================================

using 'main.bicep'

param environmentName = 'prod'
// Read from the environment, defaulting to the Day 23 value - the same
// pattern main.dev.bicepparam uses, and for a reason now confirmed rather
// than hypothetical: a real dev deploy against southindia failed with
// "ProvisioningDisabled: Subscriptions are restricted from provisioning in
// this region" on Azure SQL (Days/day-24). Left as the hardcoded literal,
// this file would carry the identical, already-diagnosed failure into prod
// the first time anyone tried to deploy it for real.
param location = readEnvironmentVariable('AZURE_LOCATION', 'southindia')
param resourceGroupName = 'rg-quotes-prod'
param namePrefix = 'quotes'

param tags = {
  costCentre: 'thinkschool'
  owner: 'shruti'
  dataClassification: 'internal'
}

// -----------------------------------------------------------------------------
// SQL
// -----------------------------------------------------------------------------

// A group, not a person. The dev server can name an individual as admin because
// losing access to it costs an afternoon; a production server whose only
// administrator is one leaver's account is an outage waiting for a resignation.
param sqlAadAdminLogin = readEnvironmentVariable('SQL_AAD_ADMIN_LOGIN', '')
param sqlAadAdminObjectId = readEnvironmentVariable('SQL_AAD_ADMIN_OBJECT_ID', '')
param sqlAadAdminPrincipalType = 'Group'

// General Purpose Gen5, 2 vCores. The jump off Basic is not about size - it is
// that Basic caps at 2 GB, keeps 7 days of backups, and has no read scale-out or
// point-in-time restore window worth the name.
param sqlDatabaseSku = {
  name: 'GP_Gen5_2'
  tier: 'GeneralPurpose'
  family: 'Gen5'
  capacity: 2
}
param sqlDatabaseMaxSizeBytes = 34359738368 // 32 GB

// Still true, and still the weakest link in this stack. The honest fix is VNet
// integration plus a private endpoint, which is a larger change than this
// exercise covers - named in the write-up rather than left as a silent default.
param sqlAllowAzureServices = true

// -----------------------------------------------------------------------------
// Service Bus
// -----------------------------------------------------------------------------

// Premium buys three things that matter here and nothing that does not:
// dedicated capacity (so a noisy neighbour cannot slow the audit trail),
// zone redundancy, and a message size ceiling above Standard's 256 KB.
param serviceBusSku = 'Premium'
param serviceBusCapacity = 1
param serviceBusTopicName = 'quote-events'

// Identical topology to dev, deliberately. A subscription or filter that exists
// in only one environment is a bug that can only be found in the environment
// where it is missing.
param serviceBusSubscriptions = [
  {
    name: 'search-indexer'
    sqlFilter: 'eventType = \'QuoteCreated\''
    maxDeliveryCount: 3
    lockDurationSeconds: 60
  }
  {
    name: 'audit-log'
    sqlFilter: ''
    maxDeliveryCount: 3
    lockDurationSeconds: 60
  }
]

// -----------------------------------------------------------------------------
// Managed environment
// -----------------------------------------------------------------------------

// Same mechanism as dev, opposite expectation. Unset - the default, and what
// this file leaves it as - prod creates its own managed environment, which is
// what a production subscription should do: an app sharing dev's environment
// shares its platform upgrades and its outages, and inherits a blast radius
// nobody sized for production.
//
// It is parameterised here at all, rather than hardcoded empty, because the
// asymmetry would itself be the bug - a template where only one environment can
// borrow is a template that cannot be exercised the way it will be run. Set it
// and prod borrows too; nothing here decides that on prod's behalf.
// Explicit, not the template default of '<namePrefix>-api'. A container app
// name must be unique within its *managed environment*, not within its resource
// group - so the moment an environment can be shared, 'quotes-api' collides
// with the live app of that exact name. Naming the app per environment makes
// the borrowed and owned paths behave identically instead of one of them being
// a latent hostname conflict. See main.dev.bicepparam.
param apiName = 'quotes-api-prod'

param existingManagedEnvironmentId = readEnvironmentVariable('EXISTING_CONTAINERAPP_ENV_ID', '')

// Only ever consulted when the line above is set - main.bicep ignores it
// otherwise, so a stale value cannot quietly move a prod environment. See
// main.dev.bicepparam, where it is load-bearing.
param apiLocation = readEnvironmentVariable('API_LOCATION', '')

// -----------------------------------------------------------------------------
// API
// -----------------------------------------------------------------------------

// No placeholder default. In prod, deploying "whatever image the parameter file
// happened to name last" is the failure mode; an unset variable failing the
// deployment outright is the desired behaviour.
param apiContainerImage = readEnvironmentVariable('API_CONTAINER_IMAGE')
param containerRegistryLoginServer = readEnvironmentVariable('ACR_LOGIN_SERVER', '')
param containerRegistryResourceId = readEnvironmentVariable('ACR_RESOURCE_ID', '')

// Two replicas minimum: one for availability during a revision rollout, and
// because scale-to-zero would put a cold start in front of a real user.
//
// It also changes behaviour, not just capacity. Day 21's HybridCache
// deduplicates a stampede *per process* - with two replicas, a cold cache under
// load costs two factory invocations, not one. That is still a 200-to-2
// reduction, but it is not the single-hit guarantee the single-instance test
// proves, and pretending otherwise is how a cache gets blamed for a database
// spike nobody can reproduce.
param apiMinReplicas = 2
param apiMaxReplicas = 10
param apiCpu = '1.0'
param apiMemory = '2Gi'
param apiConcurrentRequests = 100

param apiSchemaBootstrap = 'Migrate'
param apiAspNetCoreEnvironment = 'Production'

// 90 days, so an incident review in month three still has the logs it needs.
param logAnalyticsRetentionInDays = 90
