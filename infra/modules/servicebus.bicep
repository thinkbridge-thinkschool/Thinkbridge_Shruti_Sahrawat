// =============================================================================
// Service Bus namespace, one topic, N subscriptions - Day 19's topology as code.
//
// Two things here are easy to get wrong and expensive to notice late.
//
// 1. THE $Default RULE. Creating a subscription creates a rule named `$Default`
//    holding a TrueFilter - it matches every message, which is what makes a
//    brand-new subscription receive everything. Adding a filter as a *new* rule
//    with a nice descriptive name does not replace that; Service Bus ORs the
//    rules together, so the TrueFilter keeps matching and the filter you just
//    wrote changes nothing at all. Every message still arrives, the deployment
//    succeeds, `az servicebus ... rule list` shows your rule sitting there
//    looking correct, and the only symptom is the search indexer quietly doing
//    work it was designed not to do. The fix is to overwrite `$Default` itself
//    rather than add a sibling, which is what the rule resource below does.
//
// 2. LOCAL AUTH. `disableLocalAuth: true` removes SAS keys from the namespace
//    entirely. Quotes.Worker/appsettings.json already says its ConnectionString
//    is intentionally empty because "no key exists for this namespace to leak" -
//    this is the line that makes that literally true rather than a convention
//    someone can quietly break by pasting a key into a config file.
//
// Duplicate detection is deliberately OFF. It would swallow exactly the
// republished message Day 20's outbox relay is expected to produce after a crash
// between publish and mark-sent, so the consumer-side (MessageId, Consumer)
// ledger that Day 19 built to absorb it would never be exercised - a protection
// that silently stops being tested is worse than one that was never added.
// =============================================================================

import { topicSubscription } from '../types.bicep'

@description('Namespace name. Globally unique across Azure.')
param namespaceName string

param location string

param tags object = {}

@allowed([
  'Standard'
  'Premium'
])
param skuName string

@description('Messaging units. Premium only - ignored on Standard, where capacity is not a dial.')
param capacity int = 1

param topicName string

@description('Subscriptions to create. An empty sqlFilter leaves the subscription matching everything.')
param subscriptions topicSubscription[]

@description('Principal to grant Data Sender and Data Receiver on this namespace. Empty skips the grants entirely, which is what a plan run by someone without RBAC-write permission needs.')
param principalId string = ''

@description('Default message TTL, ISO 8601. 14 days is the Service Bus maximum for Standard.')
param defaultMessageTimeToLive string = 'P14D'

// Built-in role definition IDs. Constants, identical in every tenant - verify
// with `az role definition list --name "Azure Service Bus Data Sender"`.
var serviceBusDataSenderRoleId = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
var serviceBusDataReceiverRoleId = '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'

resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: namespaceName
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: skuName
    capacity: skuName == 'Premium' ? capacity : null
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    zoneRedundant: skuName == 'Premium'
  }
}

resource topic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: namespace
  name: topicName
  properties: {
    defaultMessageTimeToLive: defaultMessageTimeToLive
    enableBatchedOperations: true
    requiresDuplicateDetection: false
    supportOrdering: false
  }
}

resource subscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = [
  for sub in subscriptions: {
    parent: topic
    name: sub.name
    properties: {
      // Once delivery count passes this, the broker dead-letters the message
      // itself. Day 19's handlers dead-letter unrecoverable messages on the
      // first delivery instead of waiting for this - both routes end in the
      // DLQ, and the run recorded one of each.
      maxDeliveryCount: sub.maxDeliveryCount
      lockDuration: 'PT${sub.lockDurationSeconds}S'
      deadLetteringOnMessageExpiration: true
      // A filter that throws (a message missing the property the filter reads)
      // is a routing bug, not a message to drop. Dead-lettering it keeps the
      // evidence; the alternative silently discards it.
      deadLetteringOnFilterEvaluationExceptions: true
      enableBatchedOperations: true
    }
  }
]

// Overwrites the auto-created TrueFilter rather than adding a second rule beside
// it - see the note at the top of this file. Addressed by full name instead of
// `parent:` because the parent is itself a loop, and a child resource in a loop
// cannot use `parent:` to point at one iteration of another loop.
resource defaultRule 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2022-10-01-preview' = [
  for (sub, i) in subscriptions: if (!empty(sub.sqlFilter)) {
    name: '${namespaceName}/${topicName}/${sub.name}/$Default'
    properties: {
      filterType: 'SqlFilter'
      sqlFilter: {
        // Matches an application property promoted onto the message by
        // ServiceBusQuoteEventPublisher. A filter cannot read the body - a
        // routing key that only exists inside the JSON payload is invisible to
        // the broker no matter how correct it looks.
        sqlExpression: sub.sqlFilter
        requiresPreprocessing: false
      }
    }
    dependsOn: [
      subscription[i]
    ]
  }
]

resource senderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  scope: namespace
  name: guid(namespace.id, principalId, serviceBusDataSenderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataSenderRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

resource receiverAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  scope: namespace
  name: guid(namespace.id, principalId, serviceBusDataReceiverRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataReceiverRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

output namespaceName string = namespace.name
output fullyQualifiedNamespace string = '${namespace.name}.servicebus.windows.net'
output topicName string = topic.name
output namespaceResourceId string = namespace.id
