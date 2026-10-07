param location string
param name string
param publisherEmail string
param tenantId string
param audiences array
@description('[{ name, path, url, requireJwt }]')
param backends array
param tags object

// Consumption tier: first 1M calls/month free, no fixed cost.
resource apim 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'Consumption', capacity: 0 }
  identity: { type: 'SystemAssigned' }
  properties: {
    publisherEmail: publisherEmail
    publisherName: 'MyEc'
  }
}

resource globalPolicy 'Microsoft.ApiManagement/service/policies@2024-05-01' = {
  parent: apim
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies><inbound><cors allow-credentials="false"><allowed-origins><origin>*</origin></allowed-origins><allowed-methods><method>*</method></allowed-methods><allowed-headers><header>*</header></allowed-headers></cors></inbound><backend><forward-request /></backend><outbound /><on-error /></policies>'
  }
}

module apis 'apim-api.bicep' = [for b in backends: {
  name: 'apim-api-${b.name}'
  params: {
    apimName: apim.name
    name: b.name
    path: b.path
    serviceUrl: b.url
    requireJwt: b.requireJwt
    tenantId: tenantId
    audiences: audiences
  }
  dependsOn: [ globalPolicy ]
}]

output gatewayUrl string = apim.properties.gatewayUrl
