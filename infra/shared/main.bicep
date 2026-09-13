/*
  Shared infrastructure: the things both environments use and neither owns.

  Today that is the container registry. It was created by hand during the move
  to a new subscription and has sat in the "by hand" column of
  docs/migrate-to-new-subscription.md ever since - a thing that has to be
  remembered rather than run.

  It is deliberately NOT in infra/main.bicep, and that is the interesting part.
  azure.yaml sets actionOnUnmanage.resourceGroups: delete, so `azd down` on an
  environment deletes that environment's whole resource group. A registry
  belonging to the dev stack would therefore be destroyed by tearing down dev -
  taking with it the image prod pulls from, so prod would keep serving from its
  running revision and fail the moment it tried to start a new one. The failure
  would land on the environment nobody touched, minutes to days after the
  action that caused it.

  Putting it in both stacks is worse: two stacks would each believe they manage
  it, and whichever was torn down first would take it.

  So it lives on its own, with a lifecycle that outlives both environments.
  That is also why this is a plain subscription deployment rather than a
  deployment stack: a stack's value is clean teardown, and the defining property
  of this template is that it is not torn down.
*/

targetScope = 'subscription'

@description('Region for the shared resource group and everything in it.')
param location string

@description('Resource group for shared infrastructure. Must not be either environment stack resource group - see the note above.')
param resourceGroupName string = 'rg-quotes-shared'

@description('''
Registry name - globally unique, alphanumeric only.

Set explicitly rather than derived, so that re-running this template adopts the
registry that already exists instead of creating a second one next to it. A
uniqueString() default would be reproducible and wrong: it would mint a new
registry, leave the old one holding every image, and quietly change the
ACR_LOGIN_SERVER the pipeline needs.
''')
@minLength(5)
@maxLength(50)
param registryName string

@description('Basic is enough: one repository, a handful of tags, no geo-replication. Premium is what private endpoints would need, and the registry is not on the data-tier side of Day 27.')
@allowed(['Basic', 'Standard', 'Premium'])
param registrySku string = 'Basic'

param tags object = {}

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module registry 'registry.bicep' = {
  scope: rg
  name: 'registry'
  params: {
    name: registryName
    location: location
    sku: registrySku
    tags: tags
  }
}

output registryName string = registry.outputs.name
output registryLoginServer string = registry.outputs.loginServer
output registryResourceId string = registry.outputs.resourceId
