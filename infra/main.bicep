targetScope = 'resourceGroup'

@minLength(3)
@maxLength(4)
@description('Name of the environment.')
param environmentName string

@minLength(1)
@maxLength(16)
@description('Short name used as a prefix for Azure resources. Keep it globally unique where required.')
param appShortName string

@description('Settings for the Driver Function App.')
param appSettings array = []

@description('Id of the user identity to be used for testing and debugging. This is not required in production. Leave empty if not needed. Can optionally use deployer().objectId if manually deployed')
param userIdentityPrincipalId string = ''

@description('Indicates whether the deployment is interactive.')
param isInteractiveDeployer bool = true

var location = resourceGroup().location
var abbrs = loadJsonContent('./abbreviations.json')

// User assigned managed identity to be used by the function app to reach storage and other dependencies
// Assign specific roles to this identity in the RBAC module
var sharedManagedIdentityName = '${appShortName}-${abbrs.managedIdentityUserAssignedIdentities}${environmentName}'
module sharedManagedIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  params: {
    name: sharedManagedIdentityName
    location: location
  }
}

var dataStorageAccountName = replace('${appShortName}-${abbrs.storageStorageAccounts}d${environmentName}', '-', '')
module dataStorageAccount 'br/public:avm/res/storage/storage-account:0.32.0' = {
  params: {
    name: dataStorageAccountName
    skuName: 'Standard_LRS'
    kind: 'StorageV2'
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
    }
    tableServices: {}
  }
}
var dataSettings = [
  {
    name: 'StorageTableOptions__FullyQualifiedDomainName'
    value: dataStorageAccount.outputs.serviceEndpoints.table
  }
]

module app 'function-app.bicep' = {
  params: {
    appShortName: appShortName
    instanceMemoryMB: 2048
    httpPerInstanceConcurrency: 2
    userAssignedManagedIdentityName: sharedManagedIdentity.outputs.name
    environmentName: environmentName
    appSettings: concat(appSettings, dataSettings)
    userIdentityPrincipalId: userIdentityPrincipalId
    isInteractiveDeployer: isInteractiveDeployer
  }
}

module rbac 'rbac.bicep' = {
  params: {
    userIdentityPrincipalId: userIdentityPrincipalId
    managedIdentityPrincipalId: sharedManagedIdentity.outputs.principalId
    storageAccountNames: [dataStorageAccount.outputs.name]
  }
}

output functionAppName string = app.outputs.functionAppName
output dataStorageAccountName string = dataStorageAccount.outputs.name
output appInsightsName string = app.outputs.appInsightsName
output keyVaultName string = app.outputs.keyVaultName
