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
// Managed environment
// -----------------------------------------------------------------------------

// Borrowed, not created. This subscription allows exactly one Container Apps
// managed environment and the live quotes-api already occupies it, so the first
// real deployment of this stack failed at
// MaxNumberOfGlobalEnvironmentsInSubExceeded with three of four resource types
// already up. Pointing the app at the environment that exists is what makes a
// complete deployment of this template possible at all here - not a shortcut
// around a cost.
//
// Read from the environment rather than written down, because the ID contains a
// subscription ID and because `azd-provision.ps1 -ReuseManagedEnvironment`
// discovers it with `az containerapp env list` and sets it. Unset, this template
// reverts to Day 23's behaviour and creates its own - correct for any
// subscription with room, and the behaviour every earlier day's verification
// was recorded against.
// Explicit, not the template default of '<namePrefix>-api'.
//
// This is the one genuinely dangerous detail in the whole reuse mechanism. A
// container app's name must be unique within its *managed environment*, not
// within its resource group, and its default hostname is derived from it -
// so a second app called 'quotes-api' joining the environment the live
// quotes-api already runs in either fails outright or contends for
// quotes-api.<env-domain>, which is the hostname the Static Web App proxies
// /api/* to. A different resource group does not separate them; only the name
// does. Deploying into shared infrastructure means the naming has to stop
// assuming the stack is alone in it.
param apiName = 'quotes-api-dev'

param existingManagedEnvironmentId = readEnvironmentVariable('EXISTING_CONTAINERAPP_ENV_ID', '')

// A container app must live in its environment's region. The borrowed
// environment is in southindia; the rest of this stack cannot be, because
// southindia refuses to provision a new Azure SQL server for this subscription
// (see `location` above). So these two genuinely differ - for exactly as long
// as the environment is borrowed. main.bicep ignores this value entirely when
// existingManagedEnvironmentId is empty, so a leftover setting cannot move a
// stack-owned environment and its workspace somewhere nobody asked for.
param apiLocation = readEnvironmentVariable('API_LOCATION', '')

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

// EnsureCreated, not Migrate - and this is a retreat from what this file said
// before, for a reason the first real runtime test of this stack uncovered.
//
// The old comment here read: "Migrate, not EnsureCreated. This database is
// created by this template, so it starts empty and every migration -
// including AddOutbox - applies cleanly." The first half is true. The last
// three words are not, and nothing before Day 24 could have caught it,
// because no deployment of this template had ever actually started the real
// image.
//
// Every migration in QuotesApi/Migrations was generated against **SQLite**:
//
//   Id        = table.Column<int>(type: "INTEGER")
//   Author    = table.Column<string>(type: "TEXT", maxLength: 200)
//   CreatedAt = table.Column<DateTime>(type: "TEXT")
//
// INTEGER and TEXT are SQLite storage classes; SQL Server wants int,
// nvarchar(200) and datetime2. There is no SQL Server migration set in the
// repo at all. So with Database__Provider=SqlServer, EF builds the model
// under SQL Server conventions, compares it against a snapshot produced under
// SQLite, finds a mismatch, and throws PendingModelChangesWarning before
// applying anything - which is EF doing exactly the right thing. The
// container crash-looped on it (Days/day-24, Finding 17).
//
// EnsureCreated builds the schema from the current model instead of from the
// migration set, so it is provider-correct by construction: it emits real
// SQL Server DDL, including OutboxMessages. It works here specifically
// because quotesdb is genuinely empty - Day 20's warning that EnsureCreated
// is a no-op against a database that already has tables is still true, and is
// why this is a dev-only answer.
//
// What it costs, stated rather than buried: EnsureCreated writes no
// __EFMigrationsHistory, so this database cannot later be switched to
// Migrate without being dropped or baselined by hand. The proper fix is a
// provider-specific migration set (Migrations/SqlServer alongside
// Migrations/Sqlite, selected by MigrationsAssembly at runtime), after which
// this parameter goes back to 'Migrate'. That is an application change, not
// an infrastructure one, and it is tracked as such rather than smuggled into
// a deployment exercise.
param apiSchemaBootstrap = 'EnsureCreated'
param apiAspNetCoreEnvironment = 'Production'

// Required, no default. This template forces ASPNETCORE_ENVIRONMENT=Production
// on the container regardless of environment (see main.bicep), and QuotesApi
// refuses to start in Production without a signing key of at least 32 UTF-8
// bytes - so an unset value here means the container app deploys, reports
// Succeeded, and then crash-loops on its very first line of Main(), never
// reaching SQL or Service Bus at all. That happened on this exact environment
// before this parameter existed (Days/day-24). No default is deliberate, the
// same reasoning as apiContainerImage in prod: fail at `bicep build-params`,
// not three minutes into a container restart loop in Azure.
//
// Generate one and set it once - never in this file, never committed:
//   $bytes = [byte[]]::new(48)
//   [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
//   azd env set JWT_SIGNING_KEY ([Convert]::ToBase64String($bytes))
//
// Create().GetBytes(), not the static Fill(). Fill() is .NET Core only and
// does not exist on the .NET Framework that Windows PowerShell 5.1 runs on -
// which is the shell most people here actually have, and which this file
// previously told them to use. It fails in the worst available way: the
// Fill() line throws, $bytes stays all zeros because [byte[]]::new zeroes it,
// and the very next line reports success while storing the base64 of 48 zero
// bytes - a signing key anyone can reproduce in one line. Found by running
// the documented command on PowerShell 5.1 and watching it half-fail
// (Days/day-26). Create().GetBytes() works on both.
param apiJwtSigningKey = readEnvironmentVariable('JWT_SIGNING_KEY')

param logAnalyticsRetentionInDays = 30
