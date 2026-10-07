extension microsoftGraphV1

@description('Unique, idempotent key for the app registration.')
param uniqueName string
param displayName string

@description('Object id that gets the Catalog.Admin role (the deploying user).')
param adminPrincipalId string = ''

var scopeId = guid(uniqueName, 'access_as_user')
var adminRoleId = guid(uniqueName, 'Catalog.Admin')
var azureCliAppId = '04b07795-8ddb-461a-bbee-02f9e1bf7b46'

resource app 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: uniqueName
  displayName: displayName
  signInAudience: 'AzureADMyOrg'
  identifierUris: [ 'api://${tenant().tenantId}/${uniqueName}' ]
  api: {
    requestedAccessTokenVersion: 2
    oauth2PermissionScopes: [
      {
        id: scopeId
        value: 'access_as_user'
        type: 'User'
        isEnabled: true
        adminConsentDisplayName: 'Access ${displayName}'
        adminConsentDescription: 'Call the e-commerce APIs as the signed-in user.'
        userConsentDisplayName: 'Access ${displayName}'
        userConsentDescription: 'Call the e-commerce APIs on your behalf.'
      }
    ]
    // Lets you get test tokens with: az account get-access-token --scope <entraScope>
    preAuthorizedApplications: [
      { appId: azureCliAppId, delegatedPermissionIds: [ scopeId ] }
    ]
  }
  appRoles: [
    {
      id: adminRoleId
      value: 'Catalog.Admin'
      displayName: 'Catalog Admin'
      description: 'Manage products and see all orders.'
      allowedMemberTypes: [ 'User', 'Application' ]
      isEnabled: true
    }
  ]
}

resource sp 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: app.appId
}

resource adminAssignment 'Microsoft.Graph/appRoleAssignedTo@v1.0' = if (!empty(adminPrincipalId)) {
  appRoleId: adminRoleId
  principalId: adminPrincipalId
  resourceId: sp.id
}

output clientId string = app.appId
output appIdUri string = 'api://${tenant().tenantId}/${uniqueName}'
