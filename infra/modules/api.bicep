// =============================================================================
// The API: a Container App, its environment, and the workspace its logs go to.
//
// The workspace and the managed environment live here rather than in their own
// module because neither is independently useful - a container apps environment
// cannot be created without a Log Analytics workspace to point at, and neither
// outlives the app in any scenario this stack has. A module per resource would
// be filing, not structure.
//
// No secrets. The SQL connection string below carries no password because it
// authenticates as a managed identity, and the `User Id` in it is the
// identity's *client* ID - the value that tells the driver which identity to
// ask for a token when more than one is attached to the app. It is not a
// credential and does not need @secure().
// =============================================================================

@description('Container app name.')
param name string

param location string

param tags object = {}

@description('Name of the managed environment to create.')
param environmentName string

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

@description('Port the container listens on. 8080 is what QuotesApi\'s Dockerfile exposes.')
param targetPort int = 8080

// Exactly the shape azure.yaml already uses in the deployed app, reproduced here
// so this template describes the running system rather than a tidier one.
var sqlConnectionString = 'Server=tcp:${sqlServerFqdn},1433;Database=${sqlDatabaseName};Authentication=Active Directory Managed Identity;User Id=${identityClientId};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'

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
    // Reaches every Azure SDK client in the process (SQL, Service Bus) and tells
    // DefaultAzureCredential which identity to use. Without it, a container with
    // more than one identity attached gets an ambiguous-identity failure at
    // runtime, not at deploy time.
    name: 'AZURE_CLIENT_ID'
    value: identityClientId
  }
]

var serviceBusEnv = empty(serviceBusFqdn) ? [] : [
  {
    name: 'ServiceBus__FullyQualifiedNamespace'
    value: serviceBusFqdn
  }
]

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
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

resource managedEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        // listKeys at deploy time rather than a parameter: the key is never
        // written down, never passed on a command line and never lands in a
        // deployment-history parameter record.
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

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
    environmentId: managedEnvironment.id
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
output managedEnvironmentId string = managedEnvironment.id
output logAnalyticsWorkspaceId string = logAnalytics.id
