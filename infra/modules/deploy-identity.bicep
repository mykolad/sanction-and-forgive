// The identity GitHub Actions deploys as (bootstrap.bicep). Contributor on this resource group is enough: platform.bicep
// and app.bicep create no role assignments, and attaching the app's user-assigned identity is a Contributor action.
// Its database rights (ddladmin, read/write, for the migrations) come from infra/sql/grant-identities.sql.
param location string

@description('Full OIDC subject GitHub sends for the deploy job, e.g. repo:owner@1/repo@2:environment:production.')
param githubSubject string

var contributorRoleId = 'b24988ac-6180-42a0-ab88-20f7382dd24c'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'sanction-and-forgive-github-cd'
  location: location
}

resource github 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: identity
  name: 'github-production'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: githubSubject
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

resource contributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, identity.id, contributorRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', contributorRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output clientId string = identity.properties.clientId
output principalId string = identity.properties.principalId
output name string = identity.name
