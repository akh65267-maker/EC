param location string
param name string
param senderPrincipalId string
param receiverPrincipalId string
param tags object

var senderRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2552a39d')
// Data Owner (scoped to the queue only) so KEDA can read queue length to scale Order from zero.
var ownerRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '090c5cfd-751d-490a-894a-3ce6f1109419')

resource sb 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'Basic', tier: 'Basic' } // queues only, ~$0.05 per million operations
  properties: {
    disableLocalAuth: true // Entra ID only
    minimumTlsVersion: '1.2'
  }
}

resource queue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: sb
  name: 'basket-checkout'
  properties: {
    maxDeliveryCount: 5
    lockDuration: 'PT1M'
  }
}

resource sender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: queue
  name: guid(queue.id, senderPrincipalId, senderRole)
  properties: { roleDefinitionId: senderRole, principalId: senderPrincipalId, principalType: 'ServicePrincipal' }
}

resource receiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: queue
  name: guid(queue.id, receiverPrincipalId, ownerRole)
  properties: { roleDefinitionId: ownerRole, principalId: receiverPrincipalId, principalType: 'ServicePrincipal' }
}

output name string = sb.name
output fqns string = '${sb.name}.servicebus.windows.net'
output queueName string = queue.name
