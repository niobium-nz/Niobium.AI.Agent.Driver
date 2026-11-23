# Niobium.AI.Agent.Driver

Driver implements the Stage 1 Agent Gateway: a .NET 10 isolated Azure Functions v4 app with native Streamable HTTP MCP tools `list_agents` and `dispatch_agent` at `/runtime/webhooks/mcp`. It submits bounded handoffs to published ChatGPT Workspace Agents and acknowledges submission, not completion. There is no job ledger, completion polling, callback or background recovery.

**Dispatch is disabled by default in both the application and infrastructure.** Code deployment is not authorization to enable billable agent execution. Complete the real OAuth, ingress and nonproduction acceptance gates before activation. The [handover](docs/niobium-agent-gateway-handover/Niobium.AgentGateway.Implementation-Plan.md) defines the scope; Stage 2 is not started.

The scaffold deployment check is `GET /health`. A ready instance returns HTTP 200, `status` equal to `ready`, and the Git commit embedded in the binary. This is not evidence that MCP authentication or upstream execution works. The original environment setup comes from [Niobium.Invoicing at 0b4ffa7](https://github.com/niobium-nz/Niobium.Invoicing/tree/0b4ffa7a7c6d255e7d3f0db7553cc87a807959a9).

## Development and operating guides

Start with [implementation progress](docs/implementation-progress.md) and [compatibility evidence](docs/compatibility-evidence.md), which distinguish authored code from tests actually observed. The [architecture](docs/architecture.md), [configuration and development guide](docs/configuration-and-development.md), [security boundary](docs/security.md), [ChatGPT agent setup](docs/chatgpt-agent-setup.md) and [delivery/operations runbook](docs/delivery-and-operations.md) cover the implementation and activation requirements.

Follow the [implementation patterns](AGENTS.md) for application changes and the [review and approval procedure](docs/pr-approval-handoff.md) for delivery. Preserve the independent review and coverage gates.

## Local development

Install a .NET 10 SDK compatible with `global.json`, Node.js 22+, Python 3, Azure CLI/Bicep, Azure Functions Core Tools v4 and Azurite. The existing delivery tooling also uses PowerShell 7. Run these commands inside a Git checkout:

```sh
npm install --global azurite@3.35.0 azure-functions-core-tools@4
dotnet build Niobium.AI.Agent.Driver.slnx --configuration Release
dotnet test --project src/Niobium.AI.Agent.Driver.Server.Tests/Niobium.AI.Agent.Driver.Server.Tests.csproj --configuration Release --no-build --no-restore --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory artifacts/unit
RUNNER_TEMP="${TMPDIR:-/tmp}" bash scripts/verify-gateway-local.sh
python scripts/check-coverage.py artifacts/unit/coverage.cobertura.xml artifacts/local-integration/coverage.cobertura.xml
node --test scripts/delivery-policy.test.cjs
python -m unittest discover -s scripts -p 'test_*.py'
```

The verification script starts and stops its own local Azurite and Functions processes; ports 10000-10002 and 7071 must be free. It tests actual Niobium Table persistence, application composition and native MCP wire results with dispatch disabled. It cannot certify deployed OAuth or live Workspace Agent acceptance. Niobium's process-wide module guards require one shared host per integration-test process.

For an interactive local host instead, start `azurite` separately with its data directory outside the checkout, then:

```sh
cd src/Niobium.AI.Agent.Driver.Server
FUNCTIONS_WORKER_RUNTIME=dotnet-isolated AzureWebJobsStorage='UseDevelopmentStorage=true' AgentGatewayOptions__Enabled=false func start
```

Open `http://localhost:7071/health`. Never put resolved tokens into source-controlled settings.

## Azure naming and resources

Use the service slug in resource groups and the compact prefix in resource names. Invoicing uses `rg-niobium-invoicing-{environment}` and `niobiuminvoicing`. Driver uses `rg-niobium-agents-{environment}` and `niobiumagents`. These are the approved names in **West US 2** (`westus2`).

| Resource | Test | Production |
| --- | --- | --- |
| Resource group | `rg-niobium-agents-test` | `rg-niobium-agents-prod` |
| Function App | `niobiumagents-func-test` | `niobiumagents-func-prod` |
| Hosting plan | `niobiumagents-plan-test` | `niobiumagents-plan-prod` |
| Functions host and package storage | `niobiumagentsststest` | `niobiumagentsstsprod` |
| Application Table storage account | `niobiumagentsstdtest` | `niobiumagentsstdprod` |
| Managed identity | `niobiumagents-id-test` | `niobiumagents-id-prod` |
| Application Insights | `niobiumagents-appi-test` | `niobiumagents-appi-prod` |
| Log Analytics | `niobiumagents-log-test` | `niobiumagents-log-prod` |
| Key Vault | `niobiumagents-kv-test` | `niobiumagents-kv-prod` |

Storage account names concatenate the prefix, `st`, a purpose letter (`s` for deployment packages or `d` for application data), and the environment. Other resources take `{prefix}-{abbreviation}-{environment}`. The deployment discovers the default HTTPS hostname from the Function App's ARM properties. Stage 1 provisions only the `AgentRegistration` and `AgentGrant` application tables, containing configuration rather than task/execution data. The deployment has no Service Bus resources or custom domain binding.

## GitHub and Azure configuration

Set repository variables `MAJOR_VERSION` and `MINOR_VERSION`. Configure `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID` as secrets in Driver's `test` and `prod` GitHub environments. The workflow supplies the region and resource names above.

Configure deployment identity federated credentials with issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, and these subjects:

- `repo:niobium-nz/Niobium.AI.Agent.Driver:environment:test`
- `repo:niobium-nz/Niobium.AI.Agent.Driver:environment:prod`

Give each deployment identity permission to manage declared resources and role assignments in its target group. If the group exists, the workflow verifies its location. If missing, the workflow creates it in West US 2, requiring group creation permission in the subscription. An administrator can create it in advance for a group-scoped deployment identity.

Azure Developer CLI (`azd`) packages the Server project, provisions Bicep modules and deploys to Linux Flex Consumption. Managed identity connects the host to storage, telemetry and Key Vault. The Niobium Table adapter receives the separate data endpoint through `StorageTableOptions__FullyQualifiedDomainName`. Keep local azd environment files and credentials out of commits.

The separate `infra/gateway-auth.bicep` template configures mandatory App Service OAuth without enabling dispatch or making the native MCP webhook anonymous. Apply it only through the reviewed activation procedure, which also adapts health probes, restricts scaffold CORS and proves the actual ChatGPT OAuth flow.

## Actions and activation

- **Azure Deployment** builds/tests feature pushes, compiles infrastructure, runs unit/Azurite/native MCP checks and enforces 90% combined and 80% per-module line coverage. The candidate job deploys to `test`, checks the commit, runs the standalone Deployment.Tests MSTest project and uploads health/TRX evidence. A separate runner repeats the live health check from reviewed `main` code. Both must pass the required deployed integration gate. A merge to `main` deploys the merge commit to `prod` with read-only verification.
- **PR readiness** runs trusted policy from `main` and authenticates the workflow and independent deployed check. The [approval guide](docs/pr-approval-handoff.md) explains required independent approval and administrative activation of deployment-workflow changes.
- **Package Publish** additionally requires `PUBLISH_CONTRACTS=true`. Contracts/Core are currently non-packable project references. Publishing needs a separate reviewed packaging decision, `NUGET_USER` and an appropriate NuGet trusted-publishing policy; setting the variable alone is insufficient.

Ordinary infrastructure deployments deliberately restore `AgentGatewayOptions__Enabled=false`. Real agent activation requires a reviewed change backed by mandatory OAuth, negative authorization tests, safe telemetry and an authorized harmless nonproduction A-to-B acceptance trial. An MCP submission acknowledgment must never be represented as task completion.
