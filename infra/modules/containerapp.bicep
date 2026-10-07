param location string
param name string
param environmentId string
param image string
param acrLoginServer string
param identityId string
param identityClientId string
param appInsightsSecretUri string
param env array = []
param scaleRules array = []
param tags object

resource app 'Microsoft.App/containerApps@2025-01-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'auto' }
      registries: [ { server: acrLoginServer, identity: identityId } ]
      secrets: [
        { name: 'appinsights-cs', keyVaultUrl: appInsightsSecretUri, identity: identityId }
      ]
    }
    template: {
      containers: [
        {
          name: name
          image: image
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: concat([
            { name: 'AZURE_CLIENT_ID', value: identityClientId }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', secretRef: 'appinsights-cs' }
          ], env)
          probes: [
            { type: 'Liveness', httpGet: { path: '/health', port: 8080 }, initialDelaySeconds: 10, periodSeconds: 30 }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 2
        rules: concat([ { name: 'http', http: { metadata: { concurrentRequests: '50' } } } ], scaleRules)
      }
    }
  }
}

output fqdn string = app.properties.configuration.ingress.fqdn
