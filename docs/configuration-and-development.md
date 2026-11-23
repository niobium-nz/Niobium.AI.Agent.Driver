# Configuration and development

Start with [implementation progress](implementation-progress.md) and [compatibility evidence](compatibility-evidence.md). Do not enable dispatch merely because code builds.

## Local verification

Use the .NET 10 SDK selected by `global.json`, Node 22, Azure CLI/Bicep, Azurite 3.35.0 and Functions Core Tools 4. The workflow records the actual Core Tools version and resolved package graph.

```sh
npm install --global azurite@3.35.0 azure-functions-core-tools@4
dotnet restore Niobium.AI.Agent.Driver.slnx
dotnet build Niobium.AI.Agent.Driver.slnx --configuration Release --no-restore
dotnet test --project src/Niobium.AI.Agent.Driver.Server.Tests/Niobium.AI.Agent.Driver.Server.Tests.csproj --configuration Release --no-build --no-restore --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory artifacts/unit
RUNNER_TEMP="${TMPDIR:-/tmp}" bash scripts/verify-gateway-local.sh
python scripts/check-coverage.py artifacts/unit/coverage.cobertura.xml artifacts/local-integration/coverage.cobertura.xml
```

The local script owns and stops its Azurite/Functions processes. Ports 10000-10002 and 7071 must be free. Integration tests use the real pinned Niobium Table repository with a test-only emulator client factory; no setting redirects these mutation tests to Azure. The local MCP probe accepts loopback HTTP only and requires the gateway to stay disabled. It does not prove deployed OAuth or provider acceptance.

Do not lower the existing coverage thresholds (90% combined authored application code, 80% per application module) or add exclusions to make the change pass. Contracts and Core are included by the existing project-discovery coverage policy.

## Settings

Copy `local.settings.example.json` only for local experimentation. Never commit resolved tokens or real local settings. Azure environment variable keys use double underscores.

| Setting | Meaning |
| --- | --- |
| `AgentGatewayOptions__Enabled` | Default false; production infrastructure deliberately pins false pending reviewed activation. |
| `AgentGatewayOptions__InternalWorkspaceKey` | Trusted internal workspace alias, not model input. |
| `AgentGatewayOptions__CredentialAliases__<alias>` | Resolved Workspace Agent API token, normally an App Service Key Vault reference. Alias is allowlisted in server configuration. |
| `GatewayAuthenticationOptions__TenantId` | Single reviewed Entra tenant GUID; no common/organizations endpoint. |
| `GatewayAuthenticationOptions__Audience` | Exact delegated access-token audience for this resource. |
| `GatewayAuthenticationOptions__AllowedClientIds__0` | Reviewed client ID used by the actual ChatGPT OAuth connection. More entries use successive indexes. |
| `GatewayAuthenticationOptions__RequiredScope` | Default `AgentGateway.Dispatch`; exact delegated scope. |
| `StorageTableOptions__FullyQualifiedDomainName` | Application data account Table endpoint; do not confuse it with host storage. |

Defaults: 25 seconds overall, 8 seconds per attempt, 3 attempts, 16,384 canonical input bytes, 65,536 final provider bytes, depth 32, 1,024 total properties and 256 grant rows per listing. Conversation reuse is off unless enabled both globally and on the registration. Limits are application safety bounds, not claims about published upstream limits.

The upstream token is a Workspace Agent API token, not an OpenAI API Platform key and not the incoming OAuth token. Unresolved Key Vault references and whitespace-containing credentials fail closed. The gateway accepts single-tenant RS256 delegated tokens with validated signature, issuer, audience, lifetime, tid, oid, azp and scope. App-only roles are not an implemented identity mode.

## Operator registry command

The local command is not an MCP tool or an HTTP endpoint. Run it under an approved operator Azure identity with the data account's required Table permissions. It refuses to run inside the Function App. It builds the same repository/domain composition without starting the host listener.

```sh
scripts/configure-agent-registry.sh reviewed-registration.json
```

A create file uses one snapshot and a null expected ETag. Wire naming follows `GatewayJson.Options`; the current operator property is `expected_e_tag` (the .NET `ExpectedETag` snake-case form).

```json
{
  "expected_e_tag": null,
  "registration": {
    "workspace_key": "internal-workspace",
    "target_agent": "research-v1",
    "version": 1,
    "enabled": false,
    "display_name": "Research",
    "description": "Research in approved destinations only",
    "api_channel_id": "agtch_REPLACE_WITH_APPROVED_CHANNEL",
    "credential_alias": "research-token",
    "allow_conversation_reuse": false,
    "max_input_bytes": 16384
  }
}
```

A grant file instead supplies `grant`, with `caller_partition`, `target_agent`, `version`, `enabled`, `can_dispatch` and optional `expires_utc`. Derive the partition using `CallerContext.FromValidatedClaims` and `AgentGrant.PartitionFor` from the exact validated issuer/tenant/object/client tuple. Do not use an email or an agent's claimed name. The subject component is `oid:<lowercase-object-guid>`, tenant/client GUIDs are lowercase, and issuer must exactly match authentication configuration.

For an update, use the current ETag from a fresh approved read in `expected_e_tag`. Never use `*`, null or a stale tag to force an overwrite. The receipt contains only kind, target alias, ETag and WriteId. Read-back conflict means stop and reconcile, not blindly repeat. Changing a channel or credential alias requires a new target alias; disabling the existing alias is allowed.
