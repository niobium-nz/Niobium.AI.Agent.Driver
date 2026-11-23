using 'main.bicep'

// Explicit storage staging only. This file does not activate dispatch, polling or maintenance.
// Use only in an approved environment; ordinary azd deployments still use main.bicepparam.
param environmentName = readEnvironmentVariable('AZURE_ENV_NAME')
param appShortName = 'niobiumagents'
param isInteractiveDeployer = bool(readEnvironmentVariable('IS_INTERACTIVE_DEPLOYER', 'true'))
param tableNames = [
  'AgentRegistration'
  'AgentGrant'
  'AgentJob'
  'AgentMaintenance'
  'AgentContinuationRoute'
]
