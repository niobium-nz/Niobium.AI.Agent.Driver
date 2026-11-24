# Live gateway acceptance evidence

Observation snapshot recorded 2026-11-24 06:46:43 UTC. This activity used the repository's GitHub maintainer connection, local TEST fixtures, and read-only requests to the ARM-resolved TEST and production origins. Live setup, feature deployment and agent execution require their own approved scope. See the [runbook](live-acceptance-runbook.md) and [rollback procedure](live-activation-and-rollback.md).

## Repository and deployed-health baseline

| Check | Status | Expected and observed evidence |
| --- | --- | --- |
| GitHub baseline | PASS | Protected default branch `main` is `0072ad0631cc58b1c80c90a011a4e5404c1452a0`. PR #6 is merged. Its base was `a2b701b9e98f500092a27ac2ac8acaef2ce4ca44` and head was `b27461c32b4f5480ec1dafc7aecb3b1ce5ff1fcd`. Git verified that the tested head tree equals the merged tree. [PR #6](https://github.com/niobium-nz/Niobium.AI.Agent.Driver/pull/6) |
| PR review and eligibility | PASS | GitHub recorded eligible independent approval on the exact head and zero unresolved threads. The historical short-retention finding is resolved. Code and security reviews completed on the final head. The code summary reports zero findings. The security summary records completion. The exact-head deployment and separate readiness run succeeded. [Review summary](https://github.com/niobium-nz/Niobium.AI.Agent.Driver/pull/6#issuecomment-7097792443) · [Approval](https://github.com/niobium-nz/Niobium.AI.Agent.Driver/pull/6#pullrequestreview-6021681087) · [Readiness](https://github.com/niobium-nz/Niobium.AI.Agent.Driver/actions/runs/45164371090) |
| Last verified TEST scaffold health | PASS | The candidate and independent GET checks expected `b27461c32b4f5480ec1dafc7aecb3b1ce5ff1fcd`. The independent check from immutable base `a2b701b9e98f500092a27ac2ac8acaef2ce4ca44` completed at 2026-11-23 11:38:05 UTC with that commit ready. Artifact `14814744808` matched its GitHub SHA-256 and reports `deployed_commit=true`. The combined test gate passed at 11:38:10 UTC. [TEST run](https://github.com/niobium-nz/Niobium.AI.Agent.Driver/actions/runs/45164365763) |
| Last verified production scaffold health | PASS | The approved production GET expected `0072ad0631cc58b1c80c90a011a4e5404c1452a0` and verified it ready. The production job completed at 2026-11-23 12:32:22 UTC. Artifact `14819490701` matched its GitHub SHA-256 and reports `deployed_commit=true`. [Production run](https://github.com/niobium-nz/Niobium.AI.Agent.Driver/actions/runs/45174560405) |
| Current TEST and production health | BLOCKED | Attempted only GET `/health` at each ARM-derived default origin with redirects disabled. Both requests received HTTP CONNECT 403 from Cloud egress before reaching the app. No later TEST workflow was found and no deployment was in progress at inspection. A fresh deployed SHA and current settings still require the approved network path and operator read access. |

The historical test run reported 129 .NET unit tests, 10 local integration tests, 20 Node tests, 29 Python tests and 95.93% authored application coverage for PR #6's head. The TEST and production artifacts above record exact-commit scaffold health. [Exact-head workflow results](https://github.com/niobium-nz/Niobium.AI.Agent.Driver/pull/6)

## Effective configuration and outstanding work

The checked-in TEST and production deployment defaults in `0072ad0` are as follows. Actual values, any portal overrides and the outstanding job inventory are **BLOCKED** for both environments pending an authorized Azure read.

| Setting | Source default |
| --- | --- |
| `AgentGatewayOptions__Enabled` | false |
| `AgentGatewayOptions__EnableTrackedJobs` / `AgentGatewayOptions__EnableTrackedJobAccess` | false / false |
| `AgentGatewayOptions__EnableJobMaintenance` / `AzureWebJobs.MaintainAgentJobs.Disabled` | false / true |
| `AgentGatewayOptions__EnableProviderRunPolling` / `AgentGatewayOptions__EnableParentContinuation` | false / false |
| `AgentGatewayOptions__AllowConversationReuse` | false application default |
| Ordinary application tables | `AgentRegistration` and `AgentGrant` |
| CORS and native MCP key | Empty CORS origin list, credentialed CORS off, default native key enforcement |

GitHub can read the repository, runs, deployments evidenced by workflow jobs and artifacts, and review decisions. The assigned independent reviewer has write permission. Azure CLI is available for isolated compilation. This Cloud task has no bound Azure/Entra identity or secret, its enforced network policy has no live ingress or portal grants, and this chat offers no interactive desktop control for the ChatGPT workspace. The existing workflow's Azure federated login was verified from the run and belongs to that workflow. There were zero live mutations, new job submissions, provider observations or parent notifications in this activity. Pre-existing jobs and notification disposition remain unknown. The TEST operator owns their inventory and reconciliation, including preservation of the stable identity key and signing versions required by outstanding capabilities.

## Live gate record

The rows below use the observation timestamp at the top of this record. No new deployment was performed for these gates. The last verified TEST SHA and local configuration limits are recorded above. For the next live observation, capture the actual deployed SHA, effective flags, approved scope, actor role, UTC timestamp, sanitized action and observation, evidence reference, resolution owner and cleanup outcome.

| Gate | Status | Expected observation | Actual observation and remaining owner |
| --- | --- | --- | --- |
| 1. Interactive OAuth and protected health | BLOCKED | Real ChatGPT login, discovery, client and redirect, consent and refresh. Authorized health at the exact SHA, anonymous denial and immutable independent verification. | Reviewed current platform documentation and prepared the opt-in verifier. Direct HTTPS and interactive identity access are unavailable. The Entra and workspace owners must confirm the actual connection, and the maintainer must authorize the protected health transition. |
| 2. Ingress, delegated caller and six tools | BLOCKED | Every approved ingress rejects missing, spoofed or invalid identity. Verified A/B principals remain distinct. Six tools are advertised, and read-only listing uses the validated caller. | The real probe is pending the network path, dedicated test credentials, and approved registry-only window. The TEST and workspace operators own those inputs. |
| 3. Storage, keys, registration and grants | BLOCKED | Approved application storage and runtime/operator roles, resolved Key Vault references and conditional version-2 configuration. | Only source and CI configuration were inspected. The Azure operator must provide approved resources and verify actual configuration, inventory and authorization before changes. |
| 4. Store-only A/B result | NOT RUN | Approved A starts one harmless job. B submits the expected arithmetic result and marker. The initiating connection retrieves it and negative grants fail. | Live prerequisites and a bounded execution approval are outstanding. The workspace owner and TEST operator own publication, grants and the trial. |
| 5. Provider observation | NOT RUN | A separate approved fixture has a matching run ID and bounded GET observations. Provider state leaves the business result intact. | Pending the store-only gate and explicitly approved worker inventory, budget and trial. TEST operator. |
| 6. Exact parent conversation | NOT RUN | The timer submits a result-ready event to the bound API conversation. The originating connection reads the matching stored result. | Pending the preceding gates, approved API conversation binding and a separate execution budget. Workspace owner and TEST operator. |
| 7. Platform privacy and live rollback | BLOCKED | Ingested synthetic controls show no sensitive task, result, token, capability or provider body capture. Live rollback stops new sends and preserves access and state for issued work. | Source checks can verify lifecycle behavior with synthetic data. Live telemetry queries, effective flag verification and existing job disposition need approved Azure monitoring and operator access. |

## Source and isolated verification

Candidate `c3384d33745df758710d078484825fcf84b1bd32` is on local `feature/live-gateway-acceptance`, based on the verified merge. It includes exact-audience validation, a dual-gate read-only probe and opt-in authenticated health verification. It preserves the existing workflow, controller, feature defaults and coverage policy.

| Proof class | Status | Expected and observed result |
| --- | --- | --- |
| Source build and unit tests | PASS | Release solution build and standalone Deployment.Tests restore/build succeeded. 131 unit tests passed with zero failures or skips. The new tests cover a signed token with a differing audience and an app-only role without a delegated scope. Existing obsolete MCP schema-overload warnings remain visible. |
| Operator scripts and delivery policy | PASS | 33 Python tests and 20 Node tests passed. Negative coverage includes public health under authenticated mode, missing health credential, missing native key, unsafe CORS, incomplete discovery, inconsistent result text and reusing a session without a bearer. |
| Local real Table adapter | PASS | All 10 integration tests passed with zero failures or skips, using the pinned Azurite 3.35.0 fixture. They exercised ETags, WriteId reconciliation, competing claims, atomic result and notification state, immutable routes, unknown outcomes and physical payload removal. |
| Local native transport | PASS | The isolated Functions Core Tools 4.17.0 host negotiated MCP 2025-06-18, advertised exactly six tools with the expected schema fields and returned consistent disabled tool results. The wire log reports `mode=local-disabled-only`. |
| Authored line coverage | PASS | Combined 1,486 / 1,549 lines (95.93%). Contracts 100.00%, Core 98.46%, Server 90.17%. The existing 90% combined / 80% per-module check passed. |
| Standalone mandatory authentication Bicep | PASS | Azure CLI 2.93.0 and Bicep 0.49.2 compiled `infra/gateway-auth.bicep`. The single-public-cloud issuer warning remains visible. |
| Main infrastructure Bicep | BLOCKED | Running the existing build command reached AVM OCI module restore and received proxy 403. The infrastructure matches the verified PR #6 tree. Repeat full compilation in the approved feature workflow. |
| Rollback in isolated tests | PASS | Existing flow, maintenance and real-adapter tests verified pausing starts while retaining completion/read access, inert disabled maintenance and safe deadline-only maintenance, fenced claims, retention and holding uncertain sends. No live flags or outstanding live records were changed or certified by this test class. |

Executed in the feature worktree with .NET SDK 10.0.403, Node 24.19.0, Python 3.12 and PowerShell 7.7.0. The initial integration run passed the native wire assertions and timed out on the subsequent npm launcher version check. The successful rerun used the digest-verified official 4.17.0 release with npm's prepared cache in offline mode. The fixture and its timeouts were unchanged. The temporary local npm setting was removed after the run.

```sh
dotnet restore Niobium.AI.Agent.Driver.slnx --nologo
dotnet build Niobium.AI.Agent.Driver.slnx --configuration Release --no-restore --nologo
dotnet restore src/Niobium.AI.Agent.Driver.Deployment.Tests/Niobium.AI.Agent.Driver.Deployment.Tests.csproj --nologo
dotnet build src/Niobium.AI.Agent.Driver.Deployment.Tests/Niobium.AI.Agent.Driver.Deployment.Tests.csproj --configuration Release --no-restore --nologo
dotnet test --project src/Niobium.AI.Agent.Driver.Server.Tests/Niobium.AI.Agent.Driver.Server.Tests.csproj --configuration Release --no-build --no-restore --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory artifacts/unit --report-trx
dotnet test --project src/Niobium.AI.Agent.Driver.Integration.Tests/Niobium.AI.Agent.Driver.Integration.Tests.csproj --configuration Release --no-build --no-restore --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory artifacts/local-integration --report-trx
python3 scripts/check-coverage.py artifacts/unit/coverage.cobertura.xml artifacts/local-integration/coverage.cobertura.xml
node --test scripts/delivery-policy.test.cjs
python3 -m unittest discover -s scripts -p 'test_*.py'
az bicep build --file infra/gateway-auth.bicep --outfile /workspace/scratch/driver-acceptance-tools/driver-auth.json
az bicep build --file infra/main.bicep --outfile /workspace/scratch/driver-acceptance-tools/driver-infra.json
git diff 0072ad0 --check
```

Local TRX and Cobertura results are under `artifacts/unit` and `artifacts/local-integration`. Sanitized loopback transport output is under `artifacts/compatibility/<run-id>/mcp-wire.stdout.log`. Live rollback and platform privacy remain the separate Gate 7 record above.

## Owner authorization and remaining setup

1. Provide a scoped route to TEST ingress, approved Entra and ChatGPT access, and the Azure settings, storage and telemetry reader role. Use a restricted sanitized operator export where that can establish read-only settings, including production flags.
2. Confirm the Entra and workspace owners, actual issuer/resource/client and permitted test principals, the health-verifier identity, and the reviewed workflow transition. The disabled TEST source deployment uses a zero agent-execution budget.
3. Approve the TEST storage and Key Vault staging, workspace publications and connections, scoped grants, retention and exact parent route. Record the sequence, operator, time window, attempt and cost ceilings, permitted retries and stop conditions. Use the approved secret mechanism for acquisition and projection.

The owner approved source delivery after this inspection. It uses the existing TEST workflow, default-off configuration and a zero agent-execution budget. The prior verified disabled TEST build `b27461c32b4f5480ec1dafc7aecb3b1ce5ff1fcd` is the recovery reference. If recovery is needed, coordinate restoration through the approved deployment process and verify exact-commit health and effective flags. Record the exact-head test and independent review on the delivery PR.

Live gates still require actual access, role assignments, scoped configuration and budget inputs. See the [owner configuration guide](live-owner-configuration.md) for the ordered steps and choices. Production activation remains a separate decision.
