targetScope = 'resourceGroup'

@description('Existing Function App to protect. Apply while the gateway is disabled; this template never changes the MCP authorization level.')
param functionAppName string

@description('Reviewed Entra tenant GUID. Never use common, organizations or a model-supplied issuer.')
param tenantId string

@description('Entra application registration client ID for this resource server.')
param resourceClientId string

@description('Exact delegated access token audience accepted by the gateway.')
param audience string

resource app 'Microsoft.Web/sites@2023-12-01' existing = {
  name: functionAppName
}

resource auth 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: app
  name: 'authsettingsV2'
  properties: {
    platform: { enabled: true }
    globalValidation: {
      requireAuthentication: true
      unauthenticatedClientAction: 'Return401'
      redirectToProvider: 'azureactivedirectory'
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: resourceClientId
          openIdIssuer: 'https://login.microsoftonline.com/${tenantId}/v2.0'
        }
        validation: { allowedAudiences: [audience] }
      }
    }
    httpSettings: { requireHttps: true }
    login: { tokenStore: { enabled: false } }
  }
}

// Deliberately no excluded execution path, Anonymous MCP setting, secret, or activation toggle.
// OAuth discovery and actual ChatGPT client/redirect compatibility require separate acceptance.
