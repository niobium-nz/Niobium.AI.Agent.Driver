# Compatibility evidence and activation gates

Source inspection, authored tests, observed CI results and live acceptance are different evidence categories. Neither Stage 1 nor the disabled tracked-job extension claims a completed real ChatGPT OAuth connection or an authorized Workspace Agent end-to-end trial.

## Baselines and dependencies

Stage 1 PR #5 was merged as `a2b701b9e98f500092a27ac2ac8acaef2ce4ca44`; its feature head was `40c8a5f8867459f2ecd7e988f2782ab0b019653f`. The tracked-job follow-on is [PR #6](https://github.com/niobium-nz/Niobium.AI.Agent.Driver/pull/6) on `feature/agent-job-results`. Use that PR's exact final head and workflow artifacts, not an older head's green result.

Pinned framework references remain Niobium.Abstractions / Database.StorageTable 4.2.66 and Platform.Functions / Platform.StorageTable 4.2.70. Native MCP extension remains 1.6.0 with typed-result SDK 1.0.0-preview.4; the preview status remains a release risk. The optional maintenance function adds Worker.Extensions.Timer 4.3.1. JWT signing and verification use the existing Microsoft IdentityModel 8.22.0 dependency graph. No unverified beta provider-status SDK was added.

Reference surfaces inspected include [Workspace Agent triggers](https://developers.openai.com/workspace-agents/trigger-runs), [Azure MCP bindings](https://learn.microsoft.com/azure/azure-functions/functions-bindings-mcp-tool-trigger), the [MCP extension source](https://github.com/Azure/azure-functions-mcp-extension), and [Table replacement semantics](https://learn.microsoft.com/rest/api/storageservices/update-entity2). Documentation and compilation alone are not acceptance evidence.

## Historical verified Stage 1

Feature run [45147173401](https://github.com/niobium-nz/Niobium.AI.Agent.Driver/actions/runs/45147173401) observed 73 unit tests, 5 integration tests, 20 Node tests and 18 Python tests passing; 93.56% combined authored line coverage; actual pinned-adapter Azurite persistence; native MCP 2025-06-18 negotiation and disabled-mode tools; successful Azure test deployment and the independent main harness. These are historical Stage 1 results, not coverage claims for PR #6.

## Tracked-job test coverage authored

The additional unit suites exercise keyed identity and immutable request conflicts, concurrent same-ID starts, conditional write-response loss, callback-before-acknowledgment races, generation/owner/lease fencing, current completion revocation, capability signatures/expiry/key rotation/claim binding, schema/size/duplicate-key validation, late results, tombstones and rollback behavior. Maintenance tests exercise bounded pages, persistent cursor progress, competing workers, poison-row progress, never-attempted recovery and no resend after uncertain submission.

Actual-adapter integration tests use a hard-coded emulator-only connection. They test simultaneous conditional claims, actual service-side stale ETag rejection, racing callback/acceptance persistence, real one-row continuation paging and physical removal of RequestJson/ResultJson after replacement cleanup. The native local wire probe covers all six tools with execution disabled and matching structured/text results. Application composition resolves the job repositories without contacting job storage while disabled.

Initial commit c4231c2 passed build and its then-present tests but missed the unchanged coverage threshold. Subsequent test additions and SDK-fixture corrections are not certified merely by being authored. Consult the exact-head PR/workflow result for terminal counts, coverage and deployment evidence. No application-file exclusions or threshold reductions are permitted.

## Remaining live evidence

| Boundary | Still required before activation |
| --- | --- |
| ChatGPT OAuth | Actual discovery, client/redirect/refresh and delegated claims compatible with the server configuration. |
| Ingress | All reachable paths reject missing/invalid/spoofed identities; native key bypass is considered only after mandatory OAuth is independently proven. |
| Upstream | Authorized harmless nonproduction handoff and independent target receipt; preserve identical key/body semantics during any controlled retry. |
| Completion | A separate approved B connection receives the capability and submits a bounded result; unauthorized principals and wrong-job capabilities are rejected on the real host. |
| Operations/privacy | Restrictive CORS, authenticated health probes, platform telemetry redaction, approved Table/RBAC/Key Vault setup and tested rollback preserving issued callbacks. |
| Optional continuation/polling | Separate implementation, credentials/capability evidence and reviewed rollout; both remain absent. |

The authoring container lacks the .NET SDK and cannot clone GitHub over its network. Connected GitHub source writes and actual Actions evidence are used. No local .NET pass, live token use, production activation or real agent execution should be inferred from authored source or disabled local probes.
