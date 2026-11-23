using 'main.bicep'

// Reviewed staging of storage only. This does not activate dispatch, callbacks or the timer.
param environmentName = readEnvironmentVariable('AZURE_ENV_NAME')
param appShortName = 'niobiumagents'
param isInteractiveDeployer = bool(readEnvironmentVariable('IS_INTERACTIVE_DEPLOYER', 'true'))
param tableNames = [
  'AgentRegistration'
  'AgentGrant'
  'AgentJob'
  'AgentMaintenance'
]
