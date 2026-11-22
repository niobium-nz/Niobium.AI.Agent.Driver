using 'main.bicep'

param environmentName = readEnvironmentVariable('AZURE_ENV_NAME', 'dev')
param appShortName = readEnvironmentVariable('APP_SHORT_NAME')
param customDomainName = readEnvironmentVariable('CUSTOM_DOMAIN_NAME', '')
param serviceBusQueueNames = readEnvironmentVariable('SERVICE_BUS_QUEUE_NAMES', '')
param storageTableNames = readEnvironmentVariable('STORAGE_TABLE_NAMES', '')
param isInteractiveDeployer = bool(readEnvironmentVariable('IS_INTERACTIVE_DEPLOYER', 'true'))
