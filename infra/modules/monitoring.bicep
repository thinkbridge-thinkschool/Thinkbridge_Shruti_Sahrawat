/*
  Day 26's observability, expressed as infrastructure.

  Day 26 wired OpenTelemetry to Application Insights and wrote the KQL, but the
  Application Insights resource itself was never in a template - it was created
  by hand in the old subscription, and `Program.cs` picked it up from an
  APPLICATIONINSIGHTS_CONNECTION_STRING that no template ever set. That worked
  right up until the subscription changed, at which point the app kept starting,
  kept serving traffic, and quietly stopped emitting a single span. Nothing
  failed. Nothing alerted. The only symptom was an empty Logs blade in a
  resource group that no longer existed.

  That is Day 26's own finding turned on its owner: an app running without
  tracing is indistinguishable from an app tracing correctly, unless something
  asserts otherwise. So the resource moves into the stack, where it is created,
  named and connected by the same deployment that creates the app reading from
  it, and where its absence would fail a provision instead of going unnoticed.
*/

@description('Name of the Application Insights component.')
param name string

@description('Name of the Log Analytics workspace backing it.')
param workspaceName string

@description('Region. Classic (non-workspace) components were retired in February 2024, so this is workspace-based; both resources live here together.')
param location string

param tags object = {}

@description('Workspace retention. 30 days is the free tier ceiling - above it, retention is billed per GB.')
param retentionInDays int

@description('Name of the error-rate alert rule.')
param alertName string

@description('Action groups to notify. Deliberately empty by default: an action group carries a real email address, and that does not belong hardcoded in a template. The rule still evaluates and records its firing history with none attached.')
param alertActionGroupIds array = []

@description('Set false to deploy the component without the alert.')
param alertEnabled bool = true

// Workspace-based, and its own workspace rather than the one api.bicep makes.
// That one is conditional on `createsManagedEnvironment`, which is false
// wherever the stack borrows an environment - which is every environment in
// this subscription. Depending on it would mean telemetry that exists in some
// deployments and not others, decided by a flag about something else entirely.
resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
    features: {
      searchVersion: 1
    }
  }
}

resource component 'Microsoft.Insights/components@2020-02-02' = {
  name: name
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

/*
  The error-rate alert from Days/day-26/kql/error-rate-alert.kql, kept verbatim.

  The query returns rows only when the condition is breached, so the rule is
  `count > 0` and every threshold stays in the query - in version control,
  reviewable - rather than split between a query here and a number configured in
  the portal. The reasoning behind each clause (why a rate and not a count, why
  5xx only, why the total >= 20 floor) is in the .kql file, and that file is the
  copy to edit; this one has to match it.
*/
resource errorRateAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = if (alertEnabled) {
  name: alertName
  location: location
  tags: tags
  properties: {
    displayName: alertName
    description: 'Server error rate above 5% over 5 minutes, with a 20-request floor so the rate stays honest at low volume.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    scopes: [
      component.id
    ]
    criteria: {
      allOf: [
        {
          query: '''
requests
| where timestamp > ago(5m)
| summarize total = count(), failed = countif(toint(resultCode) >= 500)
| where total >= 20
| extend errorRatePct = round(100.0 * failed / total, 2)
| where errorRatePct > 5
| project errorRatePct, failed, total
'''
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: alertActionGroupIds
    }
    autoMitigate: true
  }
}

output componentName string = component.name
output componentId string = component.id
output workspaceId string = workspace.id

// No connectionString output, and that absence is deliberate. Module outputs are
// stored in the deployment history in plaintext and readable by anyone with
// read access to the subscription, and a connection string carries the
// instrumentation key. api.bicep looks the component up as an `existing`
// resource and reads the property directly instead, so the value reaches the
// container app without ever passing through a deployment record.
