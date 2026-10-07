param location string
param prefix string
param tags object

var services = [ 'catalog', 'basket', 'order' ]

resource ids 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = [for s in services: {
  name: 'id-${prefix}-${s}'
  location: location
  tags: tags
}]

output catalog object = { id: ids[0].id, name: ids[0].name, principalId: ids[0].properties.principalId, clientId: ids[0].properties.clientId }
output basket object = { id: ids[1].id, name: ids[1].name, principalId: ids[1].properties.principalId, clientId: ids[1].properties.clientId }
output order object = { id: ids[2].id, name: ids[2].name, principalId: ids[2].properties.principalId, clientId: ids[2].properties.clientId }
