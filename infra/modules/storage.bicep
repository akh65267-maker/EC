param location string
param name string
param writerPrincipalId string
param tags object

var blobContributorRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var imagesContainer = 'product-images'

resource st 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowSharedKeyAccess: false // managed identity only
    allowBlobPublicAccess: true // product images are publicly readable
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: st
  name: 'default'
}

resource images 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: imagesContainer
  properties: { publicAccess: 'Blob' }
}

resource writer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: st
  name: guid(st.id, writerPrincipalId, blobContributorRole)
  properties: { roleDefinitionId: blobContributorRole, principalId: writerPrincipalId, principalType: 'ServicePrincipal' }
}

output blobEndpoint string = st.properties.primaryEndpoints.blob
output imagesContainer string = imagesContainer
