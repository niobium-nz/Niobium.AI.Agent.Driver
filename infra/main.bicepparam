using 'main.bicep'

param environmentName = readEnvironmentVariable('AZURE_ENV_NAME')
param appShortName = 'niobiumagents'
param isInteractiveDeployer = bool(readEnvironmentVariable('IS_INTERACTIVE_DEPLOYER', 'true'))
param tableNames = [
  'AgentRegistration'
  'AgentGrant'
]
