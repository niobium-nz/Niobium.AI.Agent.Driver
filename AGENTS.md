# Working in Niobium.AI.Agent.Driver

Check the working directory, branch, commit and `git status --short` before editing. Preserve unrelated changes. Use the Driver repository for implementation and the version of the Niobium framework resolved by the project's NuGet references for framework behavior. Submit changes to `main` through a feature branch and reviewed PR.

## Implementation

- Put public wire models, commands and their representation in Contracts. Give model properties business names. For Table models, annotate the actual partition and row properties with the appropriate `EntityKey` attributes. Keep ID derivation, serialized formats and file or key construction with the model. Check every affected consumer before changing a published or persisted shape.
- Keep validation, business rules and state transitions on rich domains in Core. Flows coordinate domains, repositories and external services. Put backend SDK clients, physical Table mappings and persistence operations in framework adapters. Check the existing repository API before adding another abstraction.
- Use enums for finite states or outcomes and map existing stored values explicitly. Execute I/O and mutations before testing their named results in conditions. Trace cancellation to the underlying operation.
- Preserve ETags on conditional updates. An initialized domain may have no stored row. Forced upserts discard the update precondition. Design retries and concurrency across storage and messaging around what each operation actually commits.
- Keep Functions thin. Bind inputs, invoke the relevant flow and map outcomes to transport responses. Compose dependencies and middleware at the host boundary. Trace effective authorization through middleware and the handler before exposing a route.
- Use the pinned SDK and package references, nullable annotations, project analyzers and repository EditorConfig. Cover boundary cases, errors, retries and compatibility with existing data. Exercise changed HTTP, storage or messaging behavior in the relevant integration suite.
- Use Microsoft.Testing.Platform with MSTest. The coverage gate requires 90% combined authored application line coverage and 80% per application module. Preserve these thresholds and count authored source when adding projects.

## Infrastructure

Use `rg-niobium-<service-slug>-<env>` for resource groups and
`<prefix>-<abbr>-<env>` for named service resources such as Functions and
vaults. Functions deployment packages use `<prefix>sts<env>` and application
data uses `<prefix>std<env>`. Driver binds its `AzureWebJobsStorage` Blob, Queue
and Table endpoints to the `sts` account.
Driver uses the service slug `agents` and compact prefix `niobiumagents`.
[Azure naming guide](https://github.com/niobium-nz/Niobium.DevOps/blob/f437f5a77dbec45296c0586b5bbe828d36eeb38b/docs/azure-resource-naming.md) · [Current DevOps documentation](https://github.com/niobium-nz/Niobium.DevOps/tree/main/docs)

Driver uses West US 2 and Azure's default Functions hostname. The separate
application data account starts empty. [Test and production names](README.md#azure-naming-and-resources)

Keep host resources and runtime settings in `infra/function-app.bicep`. Put service-owned resources in `infra/main.bicep` and pass derived settings through `appSettings`. Configure each environment through the same modules. Explicitly project GitHub environment inputs into the runner. Ordinary `APPSETTING_` values become Bicep settings. For `APPSETTING_KV_` variables, only names enter the parameters. The postprovision hook writes the values to Key Vault and refreshes the app's references after successful writes. Keep credentials and local Azure state outside source control.

## Delivery

Follow the [PR review and approval procedure](docs/pr-approval-handoff.md) before submitting or merging. Every feature push runs the test pipeline, including documentation changes. Address findings, repeat the checks for a changed head, and verify the merged production deployment and its read-only checks. Treat a skipped check or a previous commit's run as missing evidence. Report the commit, run URL and terminal result.
