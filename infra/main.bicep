targetScope = 'subscription'

@description('Short name used as a prefix for every resource.')
@maxLength(10)
param environmentName string = 'myec'

@description('Region for all resources. Free-trial subscriptions may block some regions; try swedencentral / westus3 if this one fails.')
param location string = 'eastus2'

@description('Email that receives budget and failure alerts.')
param alertEmail string

@description('Monthly budget (USD). Alerts at 50%, 80% and forecasted 100%.')
param budgetAmount int = 60

@description('false = infrastructure only (first pass, before images exist in ACR).')
param deployApps bool = false

@description('Container image tag pushed to ACR by deploy.ps1.')
param imageTag string = 'latest'

param budgetStartDate string = utcNow('yyyy-MM-01')

var suffix = uniqueString(subscription().id, environmentName, location)
var tags = { app: environmentName, 'azd-env-name': environmentName }

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

// ---------- Identity: Entra ID app registration + one managed identity per service ----------
module entra 'modules/entra.bicep' = {
  scope: rg
  name: 'entra'
  params: {
    uniqueName: '${environmentName}-api-${suffix}'
    displayName: '${environmentName} API'
    adminPrincipalId: deployer().objectId
  }
}

module identities 'modules/identities.bicep' = {
  scope: rg
  name: 'identities'
  params: { location: location, prefix: environmentName, tags: tags }
}

var allPrincipals = [
  identities.outputs.catalog.principalId
  identities.outputs.basket.principalId
  identities.outputs.order.principalId
]

// ---------- Observability ----------
module monitoring 'modules/monitoring.bicep' = {
  scope: rg
  name: 'monitoring'
  params: { location: location, prefix: environmentName, alertEmail: alertEmail, tags: tags }
}

// ---------- Platform ----------
module acr 'modules/acr.bicep' = {
  scope: rg
  name: 'acr'
  params: { location: location, name: 'acr${suffix}', pullPrincipalIds: allPrincipals, tags: tags }
}

module keyVault 'modules/keyvault.bicep' = {
  scope: rg
  name: 'keyvault'
  params: {
    location: location
    name: 'kv-${suffix}'
    readerPrincipalIds: allPrincipals
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    tags: tags
  }
}

module storage 'modules/storage.bicep' = {
  scope: rg
  name: 'storage'
  params: { location: location, name: 'st${suffix}', writerPrincipalId: identities.outputs.catalog.principalId, tags: tags }
}

module redis 'modules/redis.bicep' = {
  scope: rg
  name: 'redis'
  params: { location: location, name: 'redis-${suffix}', userPrincipalId: identities.outputs.basket.principalId, tags: tags }
}

module serviceBus 'modules/servicebus.bicep' = {
  scope: rg
  name: 'servicebus'
  params: {
    location: location
    name: 'sb-${suffix}'
    senderPrincipalId: identities.outputs.basket.principalId
    receiverPrincipalId: identities.outputs.order.principalId
    tags: tags
  }
}

module postgres 'modules/postgres.bicep' = {
  scope: rg
  name: 'postgres'
  params: {
    location: location
    name: 'psql-${suffix}'
    databases: [ 'catalogdb', 'orderdb' ]
    admins: [
      { objectId: identities.outputs.catalog.principalId, name: identities.outputs.catalog.name, type: 'ServicePrincipal' }
      { objectId: identities.outputs.order.principalId, name: identities.outputs.order.name, type: 'ServicePrincipal' }
      { objectId: deployer().objectId, name: deployer().userPrincipalName, type: 'User' }
    ]
    tags: tags
  }
}

module containerEnv 'modules/containerapps-env.bicep' = {
  scope: rg
  name: 'containerapps-env'
  params: {
    location: location
    name: 'cae-${environmentName}'
    logAnalyticsName: monitoring.outputs.logAnalyticsName
    tags: tags
  }
}

// ---------- Applications (second pass, after images are pushed) ----------
var entraEnv = [
  { name: 'EntraId__TenantId', value: tenant().tenantId }
  { name: 'EntraId__ClientId', value: entra.outputs.clientId }
  { name: 'EntraId__Audience', value: entra.outputs.appIdUri }
]

module catalogApp 'modules/containerapp.bicep' = if (deployApps) {
  scope: rg
  name: 'app-catalog'
  params: {
    location: location
    environmentId: containerEnv.outputs.id
    acrLoginServer: acr.outputs.loginServer
    appInsightsSecretUri: keyVault.outputs.appInsightsSecretUri
    tags: tags
    name: 'catalog-api'
    image: '${acr.outputs.loginServer}/catalog:${imageTag}'
    identityId: identities.outputs.catalog.id
    identityClientId: identities.outputs.catalog.clientId
    env: concat(entraEnv, [
      { name: 'ConnectionStrings__CatalogDb', value: 'Host=${postgres.outputs.fqdn};Database=catalogdb;Username=${identities.outputs.catalog.name};SslMode=Require' }
      { name: 'Storage__BlobEndpoint', value: storage.outputs.blobEndpoint }
      { name: 'Storage__ImagesContainer', value: storage.outputs.imagesContainer }
    ])
  }
}

module basketApp 'modules/containerapp.bicep' = if (deployApps) {
  scope: rg
  name: 'app-basket'
  params: {
    location: location
    environmentId: containerEnv.outputs.id
    acrLoginServer: acr.outputs.loginServer
    appInsightsSecretUri: keyVault.outputs.appInsightsSecretUri
    tags: tags
    name: 'basket-api'
    image: '${acr.outputs.loginServer}/basket:${imageTag}'
    identityId: identities.outputs.basket.id
    identityClientId: identities.outputs.basket.clientId
    env: concat(entraEnv, [
      { name: 'Redis__Host', value: redis.outputs.host }
      { name: 'ServiceBus__FullyQualifiedNamespace', value: serviceBus.outputs.fqns }
      { name: 'ServiceBus__QueueName', value: serviceBus.outputs.queueName }
    ])
  }
}

module orderApp 'modules/containerapp.bicep' = if (deployApps) {
  scope: rg
  name: 'app-order'
  params: {
    location: location
    environmentId: containerEnv.outputs.id
    acrLoginServer: acr.outputs.loginServer
    appInsightsSecretUri: keyVault.outputs.appInsightsSecretUri
    tags: tags
    name: 'order-api'
    image: '${acr.outputs.loginServer}/order:${imageTag}'
    identityId: identities.outputs.order.id
    identityClientId: identities.outputs.order.clientId
    env: concat(entraEnv, [
      { name: 'ConnectionStrings__OrderDb', value: 'Host=${postgres.outputs.fqdn};Database=orderdb;Username=${identities.outputs.order.name};SslMode=Require' }
      { name: 'ServiceBus__FullyQualifiedNamespace', value: serviceBus.outputs.fqns }
      { name: 'ServiceBus__QueueName', value: serviceBus.outputs.queueName }
    ])
    // Scales to zero; KEDA wakes it up when checkout messages arrive.
    scaleRules: [
      {
        name: 'checkout-queue'
        custom: {
          type: 'azure-servicebus'
          identity: identities.outputs.order.id
          metadata: {
            namespace: serviceBus.outputs.name
            queueName: serviceBus.outputs.queueName
            messageCount: '5'
          }
        }
      }
    ]
  }
}

module apim 'modules/apim.bicep' = if (deployApps) {
  scope: rg
  name: 'apim'
  params: {
    location: location
    name: 'apim-${suffix}'
    publisherEmail: alertEmail
    tenantId: tenant().tenantId
    audiences: [ entra.outputs.clientId, entra.outputs.appIdUri ]
    backends: [
      { name: 'catalog', path: 'catalog', url: 'https://${catalogApp!.outputs.fqdn}', requireJwt: false }
      { name: 'basket', path: 'basket', url: 'https://${basketApp!.outputs.fqdn}', requireJwt: true }
      { name: 'order', path: 'order', url: 'https://${orderApp!.outputs.fqdn}', requireJwt: true }
    ]
    tags: tags
  }
}

// ---------- Cost guard ----------
resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: 'budget-${environmentName}'
  properties: {
    category: 'Cost'
    amount: budgetAmount
    timeGrain: 'Monthly'
    timePeriod: { startDate: budgetStartDate }
    notifications: {
      actual50: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 50, thresholdType: 'Actual', contactEmails: [ alertEmail ] }
      actual80: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 80, thresholdType: 'Actual', contactEmails: [ alertEmail ] }
      forecast100: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 100, thresholdType: 'Forecasted', contactEmails: [ alertEmail ] }
    }
  }
}

output resourceGroup string = rg.name
output acrName string = acr.outputs.name
output acrLoginServer string = acr.outputs.loginServer
output entraClientId string = entra.outputs.clientId
output entraScope string = '${entra.outputs.appIdUri}/access_as_user'
output apiGatewayUrl string = deployApps ? apim!.outputs.gatewayUrl : ''
output productImagesUrl string = '${storage.outputs.blobEndpoint}${storage.outputs.imagesContainer}'
