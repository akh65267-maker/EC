param location string
param name string
param readerPrincipalIds array
@secure()
param appInsightsConnectionString string
param tags object

var secretsUserRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
}

resource aiSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'appinsights-connection-string'
  properties: { value: appInsightsConnectionString }
}

resource readers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for p in readerPrincipalIds: {
  scope: kv
  name: guid(kv.id, p, secretsUserRole)
  properties: { roleDefinitionId: secretsUserRole, principalId: p, principalType: 'ServicePrincipal' }
}]

output appInsightsSecretUri string = aiSecret.properties.secretUri
