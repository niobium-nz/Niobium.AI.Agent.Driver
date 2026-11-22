# Niobium.AI.Agent.Driver

Driver is intended to accept an AI agent’s MCP request and dispatch its input to a ChatGPT workspace agent through an API. Initial dispatch is fire-and-forget. The environment runs a .NET 10 isolated Azure Functions v4 app. Its deployment check is `GET /health`. A ready instance returns HTTP 200, `status` equal to `ready`, and the Git commit embedded in the binary. The environment setup comes from [Niobium.Invoicing at 0b4ffa7](https://github.com/niobium-nz/Niobium.Invoicing/tree/0b4ffa7a7c6d255e7d3f0db7553cc87a807959a9).

## Local development

Install a .NET 10 SDK compatible with `global.json`, Azure Functions Core Tools v4 and Azurite. The delivery tests also use Node.js 22+, Python 3 and PowerShell 7. Run these commands from the repository root inside a Git checkout.

```sh
dotnet build Niobium.AI.Agent.Driver.slnx --configuration Release
dotnet test --solution Niobium.AI.Agent.Driver.slnx --configuration Release --no-build --no-restore
node --test scripts/delivery-policy.test.cjs
python -m unittest discover -s scripts -p 'test_*.py'
```

Start `azurite` in a separate terminal with its data directory outside the checkout. Start the Functions host from the Server project, then open `http://localhost:7071/health`.

```sh
cd src/Niobium.AI.Agent.Driver.Server
FUNCTIONS_WORKER_RUNTIME=dotnet-isolated AzureWebJobsStorage='UseDevelopmentStorage=true' func start
```

Follow the [implementation patterns](AGENTS.md) for application changes and the [review and approval procedure](docs/pr-approval-handoff.md) for delivery.

## Azure naming and resources

Use the service slug in resource groups and the compact prefix in resource names. Invoicing uses `rg-niobium-invoicing-{environment}` and `niobiuminvoicing`. Driver uses `rg-niobium-agents-{environment}` and `niobiumagents`. The table below records the approved names in **West US 2** (`westus2`). [Invoicing test deployment](https://github.com/niobium-nz/Niobium.Invoicing/actions/runs/43632811255/job/132569821637) · [Invoicing production deployment](https://github.com/niobium-nz/Niobium.Invoicing/actions/runs/41157164240/job/124406383818)

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

Storage account names concatenate the prefix, `st`, a purpose letter (`s` for deployment packages or `d` for application data), and the environment. Other resources take `{prefix}-{abbreviation}-{environment}`. The deployment discovers the default HTTPS hostname from the Function App’s ARM properties. The separate data account starts with zero application tables. The deployment has no Service Bus resources or custom domain binding.

## GitHub and Azure configuration

Set the repository variables `MAJOR_VERSION` and `MINOR_VERSION` for release numbering. Configure `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID` as secrets in Driver’s `test` and `prod` GitHub environments. The workflow supplies the region and resource names above.

Configure the deployment identity’s federated credentials with issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, and these subjects.

- `repo:niobium-nz/Niobium.AI.Agent.Driver:environment:test`
- `repo:niobium-nz/Niobium.AI.Agent.Driver:environment:prod`

Give each deployment identity permission to manage the declared resources and role assignments in its target group. If the group already exists, the workflow verifies its location. If it is missing, the workflow creates it in West US 2, which requires group creation permission in the subscription. An administrator can create the group in advance for a group-scoped deployment identity.

The Azure Developer CLI (`azd`) packages the Server project, provisions the Bicep modules and deploys the package to Linux Flex Consumption. The managed identity connects the host to its own storage account, telemetry and Key Vault. The Niobium Table adapter receives the separate data endpoint through `StorageTableOptions__FullyQualifiedDomainName`. Keep local azd environment files and credential values out of commits.

## Actions and first activation

- **Azure Deployment** builds and tests every feature push and enforces the 90% combined and 80% per-module line coverage thresholds. The required **Deploy test and run integration** job deploys to `test`, checks the deployed commit, runs the standalone `Niobium.AI.Agent.Driver.Deployment.Tests` MSTest project and uploads the health report and TRX. The project starts with one placeholder test. A separate runner repeats the live health check from reviewed `main` code. A merge to `main` deploys the merge commit to `prod` and verifies it with a read-only request.
- **PR readiness** runs trusted policy code from `main` and posts `Test deployment verified` after authenticating the workflow and its independent deployed check. See the [approval guide](docs/pr-approval-handoff.md) for the merge requirements and how to activate changes to the deployment workflow.
- **Package Publish** uses NuGet trusted publishing when a merged change touches `src/Niobium.AI.Agent.Driver.Contracts/`. The initial host has no public Contracts package. Before its first release, configure the repository secret `NUGET_USER` and a NuGet trusted publishing policy for this repository and `package-publish.yml`.

For the first live rollout, configure the target environments and reviewer access before pushing. Record the candidate deployment and independent approval for the administrator’s first-installation decision. After installing the baseline, verify the enforced rules and a trusted deployment on a subsequent feature commit.
