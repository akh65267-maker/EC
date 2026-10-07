// Azure Managed Redis (successor of Azure Cache for Redis), Entra ID auth only.
param location string
param name string
param userPrincipalId string
param tags object

resource redis 'Microsoft.Cache/redisEnterprise@2025-04-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'Balanced_B0' } // smallest/cheapest SKU
  properties: { minimumTlsVersion: '1.2' }
}

resource db 'Microsoft.Cache/redisEnterprise/databases@2025-04-01' = {
  parent: redis
  name: 'default'
  properties: {
    clientProtocol: 'Encrypted'
    port: 10000
    clusteringPolicy: 'EnterpriseCluster'
    evictionPolicy: 'VolatileLRU'
    accessKeysAuthentication: 'Disabled'
  }
}

resource access 'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-04-01' = {
  parent: db
  name: 'basketapi'
  properties: {
    accessPolicyName: 'default'
    user: { objectId: userPrincipalId }
  }
}

output host string = '${redis.properties.hostName}:10000'
