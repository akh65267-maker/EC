param location string
param name string
param pullPrincipalIds array
param tags object

var acrPullRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}

resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for p in pullPrincipalIds: {
  scope: acr
  name: guid(acr.id, p, acrPullRole)
  properties: { roleDefinitionId: acrPullRole, principalId: p, principalType: 'ServicePrincipal' }
}]

output name string = acr.name
output loginServer string = acr.properties.loginServer
