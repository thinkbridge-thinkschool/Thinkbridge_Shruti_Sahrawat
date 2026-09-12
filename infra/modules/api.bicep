// =============================================================================
// The API: a Container App, its environment, and the workspace its logs go to.
//
// The workspace and the managed environment live here rather than in their own
// module because neither is independently useful - a container apps environment
// cannot be created without a Log Analytics workspace to point at, and neither
// outlives the app in any scenario this stack has. A module per resource would
// be filing, not structure.
//
// One secret, and it is not the SQL connection string. That string carries no
// password because it authenticates as a managed identity, and the `User Id`
// in it is the identity's *client* ID - the value that tells the driver which
// identity to ask for a token when more than one is attached to the app. It
// is not a credential and does not need @secure().
//
// The JWT signing key is a real secret, discovered the hard way: Program.cs
// refuses to start in Production without `Jwt__Key` set, and this module set
// ASPNETCORE_ENVIRONMENT=Production unconditionally for both dev and prod
// (see aspNetCoreEnvironment below) without ever supplying the one thing that
// guard requires - so every deploy of this template, dev included, crash-
// looped on that exception before ever reaching SQL. It is a Container Apps
// *secret*, referenced by env via secretRef rather than passed as a plain
// value - the same shape the live app already uses (`jwt-key`), found by
// checking rather than guessing at the fix.
// =============================================================================

@description('Container app name.')
param name string

param location string

param tags object = {}

@description('Name of the managed environment to create. Ignored when existingManagedEnvironmentId is supplied.')
param environmentName string

@description('''
Resource ID of a Container Apps managed environment that already exists, to
host this app in instead of creating one.

This is not a convenience knob. This subscription is capped at exactly one
managed environment (`MaxNumberOfGlobalEnvironmentsInSubExceeded`) and the one
it is allowed already runs the live quotes-api - so creating a second is not
slow or expensive here, it is impossible, and it is where the first real dev
deployment of this stack stopped. Supplied, this parameter makes the container
app join that environment: the app, its ingress, its identity, its revisions
and its scale rules are all still this stack's, and the environment is the one
piece of shared infrastructure it borrows rather than owns.

Empty (the default) keeps Day 23's behaviour exactly - create the environment
and its workspace as part of the stack - so nothing about the template changes
for a subscription that has room.

Two consequences worth stating rather than discovering:
  * The Log Analytics workspace is not created either. Log destination is a
    property of the environment, not the app, so a workspace this stack made
    would receive nothing; the app's logs go where the borrowed environment
    already sends them.
  * A container app must sit in its environment's region. That is what
    `apiLocation` in main.bicep exists for, and why it can differ from the
    region the rest of the stack deploys to.

The environment is deliberately NOT declared as an `existing` resource here.
Reading it would need the caller to hold permissions on a resource group this
stack does not manage, purely to look up an ID that was passed in already -
and, more to the point, `denySettings` applies to what the stack manages. A
borrowed environment must stay unmanaged by this stack, or `azd down` would
be entitled to take the live app's environment down with it.
''')
param existingManagedEnvironmentId string = ''

param logAnalyticsName string

@minValue(30)
@maxValue(730)
param logAnalyticsRetentionInDays int

@description('Resource ID of the user-assigned identity to attach.')
param identityResourceId string

@description('Client ID of that identity - goes into the connection string.')
param identityClientId string

param containerImage string

@description('Registry login server, e.g. myregistry.azurecr.io. Empty means the image is public and no registry credential block is emitted at all.')
param containerRegistryLoginServer string = ''

@minValue(0)
param minReplicas int

@minValue(1)
param maxReplicas int

param cpu string

param memory string

param concurrentRequests int

param sqlServerFqdn string

param sqlDatabaseName string

@description('Service Bus namespace FQDN. Passed to the container so the outbox relay and worker can be hosted here later without a config change; empty omits the variable.')
param serviceBusFqdn string = ''

@allowed([
  'Migrate'
  'EnsureCreated'
])
param schemaBootstrap string

param aspNetCoreEnvironment string

@description('''
Key Vault URI of the JWT signing secret, e.g.
https://kv-dev-abc123.vault.azure.net/secrets/jwt-key

Day 24 passed the key itself here as a @secure() param and stored it in the
container app's own secret store. Day 25 replaced that with a reference: this
module no longer receives, sees, or stores the signing key at all - only the
address of a secret it is allowed to read, and the identity it reads it with.

Versionless (no trailing GUID) so a rotation in the vault is picked up without
a redeployment.
''')
param jwtSecretUri string

@description('''
Resource ID of the identity the container app authenticates to Key Vault with.
Normally the same user-assigned identity as everything else in this stack -
passed separately from identityResourceId only because the platform requires
it named on the secret itself, not inherited from the app.

This identity must already hold Key Vault Secrets User on the vault before
this resource is created. Container Apps resolves the reference at create
time, not at container start, so a missing or unpropagated role assignment
fails the deployment rather than producing an app that starts and then cannot
read its key.
''')
param keyVaultIdentityResourceId string

@description('''
Name of the Application Insights component in this resource group.

Passed as a name rather than a connection string on purpose. The connection
string carries the instrumentation key, and a module output is written to the
deployment history in plaintext, readable by anyone with subscription read
access. Looking the component up as an `existing` resource keeps the value on
the path between ARM and the container app and out of every record in between.
''')
param appInsightsName string

@description('Port the container listens on. 8080 is what QuotesApi\'s Dockerfile exposes.')
param targetPort int = 8080

// Exactly the shape azure.yaml already uses in the deployed app, reproduced here
// so this template describes the running system rather than a tidier one.
var sqlConnectionString = 'Server=tcp:${sqlServerFqdn},1433;Database=${sqlDatabaseName};Authentication=Active Directory Managed Identity;User Id=${identityClientId};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'

// Not created here - modules/monitoring.bicep owns it. Declared `existing`
// only to read the connection string below.
resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

var baseEnv = [
  {
    name: 'ConnectionStrings__Default'
    value: sqlConnectionString
  }
  {
    name: 'Database__Provider'
    value: 'SqlServer'
  }
  {
    // Day 20 found this missing in the deployed environment: with nothing set,
    // Program.cs infers EnsureCreated() for SQL Server, and EnsureCreated() is
    // a no-op against a database that already has tables - so the OutboxMessages
    // table never appeared and the first POST /api/quotes after that deploy
    // would 500. Naming it here is the fix; the same setting is what
    // Quotes.Tests.Integration needed to stop the inference breaking it.
    name: 'Database__SchemaBootstrap'
    value: schemaBootstrap
  }
  {
    name: 'ASPNETCORE_ENVIRONMENT'
    value: aspNetCoreEnvironment
  }
  {
    // secretRef, not value: the actual key never appears in this array, in
    // `az containerapp show`, or in the ARM deployment's parameter history -
    // only the reference to the secret defined above does.
    name: 'Jwt__Key'
    secretRef: 'jwt-key'
  }
  {
    // Reaches every Azure SDK client in the process (SQL, Service Bus) and tells
    // DefaultAzureCredential which identity to use. Without it, a container with
    // more than one identity attached gets an ambiguous-identity failure at
    // runtime, not at deploy time.
    name: 'AZURE_CLIENT_ID'
    value: identityClientId
  }
  {
    // The Azure Monitor exporter reads this and, when it is absent, exports
    // nothing - without logging, without failing, without degrading anything a
    // health check would notice. That is how Day 26's tracing vanished the
    // moment the subscription changed: the app was fine, and only the
    // telemetry was gone. Sourced from the component this same stack creates,
    // so "deployed" and "instrumented" stop being separable states.
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: appInsights.properties.ConnectionString
  }
]

var serviceBusEnv = empty(serviceBusFqdn) ? [] : [
  {
    name: 'ServiceBus__FullyQualifiedNamespace'
    value: serviceBusFqdn
  }
]

// Both of these are created only when this stack owns its environment. They
// share one condition on purpose: an environment cannot exist without a
// workspace to point at, so there is no combination where one is wanted and
// the other is not.
var createsManagedEnvironment = empty(existingManagedEnvironmentId)

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (createsManagedEnvironment) {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: logAnalyticsRetentionInDays
  }
}

resource managedEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = if (createsManagedEnvironment) {
  name: environmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        // The `!` is a non-null assertion, and it is the correct answer here
        // rather than a way to quiet a warning. `logAnalytics` is conditional,
        // so its type is `workspace | null`, and Bicep cannot see that this
        // resource carries the *same* condition - meaning on every branch where
        // this expression is evaluated at all, the workspace exists. Without
        // the assertion this is BCP318/BCP422; with a `?? ''` fallback instead
        // it would compile and then deploy an environment wired to no
        // workspace, which is worse than either.
        customerId: logAnalytics!.properties.customerId
        // listKeys at deploy time rather than a parameter: the key is never
        // written down, never passed on a command line and never lands in a
        // deployment-history parameter record.
        sharedKey: logAnalytics!.listKeys().primarySharedKey
      }
    }
  }
}

// `managedEnvironment.id` on a conditional resource resolves to a computed
// resource ID, not a read of the resource, so this is safe on the branch where
// the resource is never deployed - the ternary picks the other side there.
var resolvedEnvironmentId = createsManagedEnvironment ? managedEnvironment.id : existingManagedEnvironmentId

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityResourceId}': {}
    }
  }
  properties: {
    environmentId: resolvedEnvironmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: targetPort
        transport: 'auto'
        allowInsecure: false
        // No corsPolicy on purpose. Day 17 links this container app as a Static
        // Web App backend, which proxies /api/* from the SPA's own origin - the
        // browser never makes a cross-origin request, so a CORS policy here
        // would be configuration that does nothing except look load-bearing.
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
      registries: empty(containerRegistryLoginServer) ? [] : [
        {
          server: containerRegistryLoginServer
          identity: identityResourceId
        }
      ]
      // Day 25: a Key Vault reference, not a value.
      //
      // What was here through Day 24 was `value: jwtSigningKey` - the key
      // itself, stored in the container app's own secret store. Masked in
      // `az containerapp show`, which is why it was defensible, but owned by
      // the app: anyone with write access to the container app could read it
      // back, it had no version history, and rotating it meant redeploying
      // the app.
      //
      // With keyVaultUrl + identity the app stores no secret value at all.
      // `az containerapp secret list` returns the URI, the identity and the
      // name, and no `value` field at all - not a masked one, not an empty
      // string, absent - because there is no value here to return. The
      // platform fetches the secret from the vault, as this identity, at
      // resolve time. Verified against the deployed app; see Days/day-25. That is the
      // difference the exercise's "prove there are zero secrets in app
      // settings" is actually asking about.
      secrets: [
        {
          name: 'jwt-key'
          keyVaultUrl: jwtSecretUri
          identity: keyVaultIdentityResourceId
        }
      ]
    }
    template: {
      containers: [
        {
          name: name
          image: containerImage
          resources: {
            // json() because Bicep has no decimal type and Container Apps wants
            // a number here, not the string "0.5".
            cpu: json(cpu)
            memory: memory
          }
          env: concat(baseEnv, serviceBusEnv)
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: targetPort
              }
              initialDelaySeconds: 15
              periodSeconds: 30
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: targetPort
              }
              initialDelaySeconds: 5
              periodSeconds: 10
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http-concurrency'
            http: {
              metadata: {
                concurrentRequests: string(concurrentRequests)
              }
            }
          }
        ]
      }
    }
  }
}

output resourceId string = containerApp.id
output fqdn string = containerApp.properties.configuration.ingress.fqdn
output name string = containerApp.name
output managedEnvironmentId string = resolvedEnvironmentId

@description('Whether this stack created the environment it runs in, or borrowed one. Surfaced as an output because it changes where to go looking for logs, and that is not something to have to re-derive from the parameter file later.')
output ownsManagedEnvironment bool = createsManagedEnvironment

@description('Empty when the environment was borrowed - the workspace belongs to whoever owns that environment.')
output logAnalyticsWorkspaceId string = createsManagedEnvironment ? logAnalytics!.id : ''
