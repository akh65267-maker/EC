param apimName string
param name string
param path string
param serviceUrl string
param requireJwt bool
param tenantId string
param audiences array

var methods = [ 'GET', 'POST', 'PUT', 'DELETE' ]
var audienceXml = join(map(audiences, a => '<audience>${a}</audience>'), '')
var jwtXml = requireJwt
  ? '<validate-jwt header-name="Authorization" failed-validation-httpcode="401" require-scheme="Bearer"><openid-config url="${environment().authentication.loginEndpoint}${tenantId}/v2.0/.well-known/openid-configuration" /><audiences>${audienceXml}</audiences></validate-jwt>'
  : ''

resource apim 'Microsoft.ApiManagement/service@2024-05-01' existing = {
  name: apimName
}

resource api 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  parent: apim
  name: name
  properties: {
    displayName: '${name} API'
    path: path
    protocols: [ 'https' ]
    serviceUrl: serviceUrl
    subscriptionRequired: false // Entra ID JWTs are the access control
  }
}

resource policy 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  parent: api
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies><inbound><base />${jwtXml}</inbound><backend><base /></backend><outbound><base /></outbound><on-error><base /></on-error></policies>'
  }
}

resource ops 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = [for m in methods: {
  parent: api
  name: toLower(m)
  properties: {
    displayName: '${m} /*'
    method: m
    urlTemplate: '/*'
  }
}]
