# Niobium.AI.Agent.Driver

Driver is a .NET 10 isolated Azure Functions v4 app with native Streamable HTTP MCP at `/runtime/webhooks/mcp`. Stage 1 `list_agents` and `dispatch_agent` provide permitted-agent discovery and bounded submission acknowledgment without storing handoffs. The separately gated [tracked-job extension](docs/tracked-jobs.md) adds `start_agent_job`, `submit_job_result`, `get_job_status` and `get_job_result`, durable conditional job state and bounded maintenance. Store-only result exchange is the default tracked mode; [provider observation and allowlisted parent continuation](docs/job-follow-ups.md) are separate opt-ins.

**Dispatch, tracked jobs, maintenance, provider polling and parent continuation are disabled by default.** Code deployment is not authorization to activate billable agent execution. Complete real OAuth, ingress, privacy and nonproduction acceptance gates before activation. The [original handover](docs/niobium-agent-gateway-handover/Niobium.AgentGateway.Implementation-Plan.md) defines the larger plan; [implementation progress](docs/implementation-progress.md) distinguishes implemented code from remaining live acceptance.

The scaffold deployment check is `GET /health`. A ready instance returns HTTP 200, `status` equal to `ready`, and the embedded Git commit. This is not evidence that MCP authentication or upstream execution works. The original environment setup comes from [Niobium.Invoicing at 0b4ffa7](https://github.com/niobium-nz/Niobium.Invoicing/tree/0b4ffa7a7c6d255e7d3f0db7553cc87a807959a9).

## Development and operating guides

Read [architecture](docs/architecture.md), [configuration](docs/configuration-and-development.md), [security](docs/security.md), [ChatGPT setup](docs/chatgpt-agent-setup.md), [delivery and operations](docs/delivery-and-operations.md), [tracked jobs](docs/tracked-jobs.md), [optional follow-ups](docs/job-follow-ups.md) and [protected gateway acceptance](docs/protected-gateway-acceptance.md). [Compatibility evidence](docs/compatibility-evidence.md) records historical test observations; the PR records final exact-head results. Follow [AGENTS.md](AGENTS.md), use `feature/<description>` branches, and retain the [independent review and approval procedure](docs/pr-approval-handoff.md). Every feature push triggers the trusted test-deployment workflow, including documentation changes.

For controlled TEST validation, use the [live acceptance runbook](docs/live-acceptance-runbook.md), [evidence record](docs/live-acceptance-evidence.md), and [activation and rollback procedure](docs/live-activation-and-rollback.md).

## Local development

Install a .NET 10 SDK compatible with `global.json`, Node.js 22+, Python 3, Azure CLI/Bicep and PowerShell 7 for the existing delivery tools. The integration suite acquires and owns pinned local Azurite 3.35.0 and Functions Core Tools 4.17.0 processes through npm. Run inside a Git checkout:

```sh
dotnet build Niobium.AI.Agent.Driver.slnx --configuration Release
dotnet test --project src/Niobium.AI.Agent.Driver.Server.Tests/Niobium.AI.Agent.Driver.Server.Tests.csproj --configuration Release --no-build --no-restore --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory artifacts/unit
bash scripts/verify-gateway-local.sh
python scripts/check-coverage.py artifacts/unit/coverage.cobertura.xml artifacts/local-integration/coverage.cobertura.xml
node --test scripts/delivery-policy.test.cjs
python -m unittest discover -s scripts -p 'test_*.py'
```

Ports 10000-10002 and 7071 must be free. The integration suite starts and stops only its own processes, tests real Niobium persistence and native MCP wire behavior with external dispatch disabled, and isolates child processes from deployment credentials. Niobium's process-wide module guards require one shared host per integration-test process. Local success cannot certify deployed OAuth or live Workspace Agent acceptance.

For an interactive local host, start `azurite` separately with its data outside the checkout, copy the example settings without enabling features, then:

```sh
cd src/Niobium.AI.Agent.Driver.Server
FUNCTIONS_WORKER_RUNTIME=dotnet-isolated AzureWebJobsStorage='UseDevelopmentStorage=true' AgentGatewayOptions__Enabled=false func start
```

Open `http://localhost:7071/health`. Never commit resolved credentials or real local settings.

## Azure naming and resources

Driver uses resource group slug `agents`, compact prefix `niobiumagents`, **West US 2** (`westus2`) and the default Azure Functions hostname.

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

Storage names concatenate prefix, `st`, purpose (`s` for deployment/host, `d` for application data) and environment. Other resources use `{prefix}-{abbreviation}-{environment}`. The ordinary parameter file provisions only `AgentRegistration` and `AgentGrant`. `infra/tracked-jobs.bicepparam` explicitly stages `AgentJob` and `AgentMaintenance`; `infra/parent-continuation.bicepparam` additionally stages `AgentContinuationRoute`. Both are operator opt-ins and leave execution disabled. No Service Bus or custom domain is provisioned by default.

## GitHub and Azure configuration

Set repository variables `MAJOR_VERSION` and `MINOR_VERSION`. Configure `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID` as secrets in Driver's `test` and `prod` GitHub environments. The workflow supplies region and resource names. Configure the deployment identity's federation using issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange` and the exact repository/environment subjects emitted by the current GitHub OIDC configuration.

Give each deployment identity permission to manage declared resources and role assignments in its target group. If a group exists, the workflow verifies West US 2. If missing, it creates the group, requiring subscription-level group creation permission; an administrator can create it first for a group-scoped identity.

Azure Developer CLI (`azd`) packages the Server, provisions Bicep and deploys Linux Flex Consumption. Managed identity connects storage, telemetry and Key Vault. The Niobium Table adapter receives the separate data endpoint through `StorageTableOptions__FullyQualifiedDomainName`. The existing APPSETTING_/APPSETTING_KV_ projection scripts remain the configuration path; keep local azd state and secrets out of source control.

`infra/gateway-auth.bicep` configures mandatory App Service OAuth without enabling dispatch or making MCP anonymous. Adopt it only through reviewed activation that also authenticates health probes and proves the actual ChatGPT OAuth flow. The reusable Function template now denies browser origins by default and disables credentialed CORS. Use the explicitly invoked read-only [authentication probe](docs/protected-gateway-acceptance.md) after supplying approved test credentials; it cannot dispatch an agent, mutate configuration or claim interactive ChatGPT consent/refresh evidence.

## Actions and activation

Azure Deployment builds/tests feature pushes, compiles infrastructure and enforces **90% combined / 80% per-module authored line coverage**. Candidate test deployment verifies the commit and runs Deployment.Tests; a separate runner repeats the read-only harness from immutable main. The trusted main controller publishes `Test deployment verified` only after its evidence checks succeed. A merged PR deploys the merge commit to production with read-only verification. An early PR readiness job can precede deployment; rerun that job after successful exact-head deployment rather than weakening the policy.

Package publication separately requires `PUBLISH_CONTRACTS=true` and a reviewed packaging change. Contracts/Core remain non-packable project references. Configure `NUGET_USER` and trusted publishing only as part of that decision.

Ordinary infrastructure restores all gateway, tracked-job and optional follow-up flags plus the timer to their disabled states. A reviewed activation change must make any authorized enablement durable. For active tracked jobs, rollback should stop new starts while retaining completion/retrieval, the compatible reader binary and necessary signing keys; see [configuration and rollback](docs/tracked-jobs.md#configuration-and-rollback). A submission acknowledgment must never be represented as task completion, and a provider run state must never substitute for the stored business result.
