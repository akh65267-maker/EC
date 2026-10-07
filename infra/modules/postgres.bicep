param location string
param name string
param databases array
@description('Entra admins: [{ objectId, name, type: ServicePrincipal|User }]')
param admins array
param tags object

resource pg 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: name
  location: location
  tags: tags
  // B1ms: 750 free hours/month for 12 months on free accounts.
  sku: { name: 'Standard_B1ms', tier: 'Burstable' }
  properties: {
    version: '16'
    storage: { storageSizeGB: 32, autoGrow: 'Disabled' }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
    authConfig: {
      activeDirectoryAuth: 'Enabled'
      passwordAuth: 'Disabled' // Entra ID / managed identity only
      tenantId: tenant().tenantId
    }
  }
}

resource allowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: pg
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

// The server accepts one change at a time, so everything below runs serially.
@batchSize(1)
resource dbs 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = [for db in databases: {
  parent: pg
  name: db
  properties: { charset: 'UTF8', collation: 'en_US.utf8' }
  dependsOn: [ allowAzure ]
}]

@batchSize(1)
resource entraAdmins 'Microsoft.DBforPostgreSQL/flexibleServers/administrators@2024-08-01' = [for a in admins: if (!empty(a.name)) {
  parent: pg
  name: a.objectId
  properties: {
    principalName: a.name
    principalType: a.type
    tenantId: tenant().tenantId
  }
  dependsOn: [ dbs ]
}]

output fqdn string = pg.properties.fullyQualifiedDomainName
