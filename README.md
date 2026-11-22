# Niobium.AI.Agent.Driver

Driver runs as a .NET 10 isolated Azure Functions v4 app. Its deployment check is `GET /health`. A ready instance returns HTTP 200, `status` equal to `ready`, and the Git commit embedded in the binary. The environment setup comes from [Niobium.Invoicing at 0b4ffa7](https://github.com/niobium-nz/Niobium.Invoicing/tree/0b4ffa7a7c6d255e7d3f0db7553cc87a807959a9).

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

## GitHub and Azure configuration

Use Driver-owned deployment targets. Configure the repository variables `MAJOR_VERSION` and `MINOR_VERSION` for the release version. Create the `test` and `prod` GitHub environments and configure each as follows.

| GitHub setting | Kind | What to supply |
| --- | --- | --- |
| `APP_SHORT_NAME` | Variable | Approved Azure resource prefix. Use 1 to 16 lowercase alphanumeric characters, starting with a letter. The infrastructure adds resource and environment suffixes. |
| `AZURE_RESOURCE_GROUP`, `AZURE_LOCATION` | Variables | Driver’s target resource group and supported Azure region. The resource-group deployment places resources in the group’s location. |
| `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | Secrets | The approved Azure deployment identity and target subscription for this environment. |
| `CUSTOM_DOMAIN_NAME` | Optional secret | A validated DNS hostname for this Function App. With no custom binding, verification uses the hostname Azure returns for the provisioned site. |
| `SERVICE_BUS_QUEUE_NAMES`, `STORAGE_TABLE_NAMES` | Optional variables | Comma-separated Driver-owned names. Empty lists create no application queues or tables. |

Set up Azure workload identity federation for each environment. The GitHub subjects are `repo:niobium-nz/Niobium.AI.Agent.Driver:environment:test` and `repo:niobium-nz/Niobium.AI.Agent.Driver:environment:prod`. Use the GitHub Actions OIDC issuer and Azure token-exchange audience. Each identity needs permission to deploy resources and create the declared role assignments in its target group. If using custom domains, complete the Azure hostname ownership and DNS setup for each before provisioning.

The Azure Developer CLI (`azd`) packages the Server project, provisions the Bicep modules and deploys the package. The host uses Linux Flex Consumption, a user-assigned managed identity, deployment blob storage, Log Analytics, Application Insights and Key Vault. The application layer configures data storage and optional Service Bus. The app and its role assignments consume outputs from the resources they depend on, including during the first deployment. Keep local azd environment files and secret values out of commits.

## Actions and first activation

- **Azure Deployment** builds and tests every feature push, enforces the 90% combined and 80% per-module line coverage thresholds, deploys to `test`, then checks the deployed commit with both the candidate and the reviewed `main` harness. A merge to `main` deploys the merge commit to `prod` and verifies it with a read-only request.
- **PR readiness** runs trusted policy code from `main` and posts `Test deployment verified` for the feature commit. The procedure for installing the first trusted harness, importing the ruleset and enabling native reviews is in the [approval guide](docs/pr-approval-handoff.md).
- **Package Publish** uses NuGet trusted publishing when a merged change touches `src/Niobium.AI.Agent.Driver.Contracts/`. The initial host has no public Contracts package. Before its first release, configure the repository secret `NUGET_USER` and a NuGet trusted publishing policy for this repository and `package-publish.yml`.

For the first live rollout, configure the target environments and reviewer access before pushing. Record the candidate deployment and independent approval for the administrator’s first-installation decision. After installing the baseline, verify the enforced rules and a trusted deployment on a subsequent feature commit.
