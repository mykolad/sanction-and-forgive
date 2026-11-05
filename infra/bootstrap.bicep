// Run once by the owner, at subscription scope (infra/README.md, "Bootstrap"). Creates the resource group and the
// identity GitHub Actions deploys as: trusted only from this repository's `production` environment, and Contributor
// on this resource group only. Everything else in the group comes from platform.bicep and app.bicep.
targetScope = 'subscription'

@description('Azure region for the resource group and everything in it.')
param location string = 'swedencentral'

@description('The resource group that holds every resource of this app.')
param resourceGroupName string = 'sanction-and-forgive-rg'

@description('GitHub OIDC subject prefix of this repository: `gh api repos/<owner>/<repo>/actions/oidc/customization/sub --jq .sub_claim_prefix`, e.g. repo:mykolad@123/sanction-and-forgive@456 (the immutable form).')
param githubSubjectPrefix string

@description('The GitHub environment the deploy workflow runs in. Only master can deploy to it.')
param githubEnvironment string = 'production'

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: {
    project: 'sanction-and-forgive'
  }
}

module deployIdentity 'modules/deploy-identity.bicep' = {
  name: 'deploy-identity'
  scope: rg
  params: {
    location: location
    githubSubject: '${githubSubjectPrefix}:environment:${githubEnvironment}'
  }
}

output resourceGroupName string = rg.name
output deployIdentityClientId string = deployIdentity.outputs.clientId
output tenantId string = tenant().tenantId
output subscriptionId string = subscription().subscriptionId
