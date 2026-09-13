using 'main.bicep'

// Central India, matching the registry that already exists and the region the
// rest of this subscription is allowed to deploy to (Azure for Students policy;
// see docs/migrate-to-new-subscription.md).
param location = 'centralindia'
param resourceGroupName = 'rg-quotes-shared'

// The registry created by hand during the subscription move. Named here so this
// template adopts it rather than standing up a second one - a registry name is
// global, and the pipeline's ACR_LOGIN_SERVER variable points at this one.
param registryName = 'crquotes33928'
param registrySku = 'Basic'

param tags = {
  workload: 'quotes'
  environment: 'shared'
  managedBy: 'bicep'
  owner: 'shruti'
  costCentre: 'thinkschool'
}
