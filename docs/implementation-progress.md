# Agent Gateway implementation progress

## Scope and baseline

Implements the Stage 1 source path from the handover on Driver baseline `56c92c72538bdcf7e48abe0a3396b6a54e0e0569`. Stage 2 is not started. The change is for PR review, not production activation or merge. PR #4 and its exact-head workflow artifacts identify the candidate; never transfer a passing result from an older head.

## Implemented source

Contracts/Core/Server layering; native list_agents and dispatch_agent; bounded deterministic handoff encoding; principal-scoped grants; current-row domain authorization; conditional operator writes with full WriteId read-back verification; fixed-origin provider client with bounded same-key retries and uncertainty preservation; independently validated delegated bearer identity; disabled-by-default composition; configuration-only Table provisioning; mandatory-auth deployment template; operator script; unit, Azurite, composition and native MCP wire tests; development/security/agent/operations runbooks.

The raw MCP binding JSON is used for dispatch arguments to avoid typed argument coercion. Current authorization is read directly by each domain from its own repository, including after explicit reload: Niobium's partition cache and entity-initialization fallback must not revive a deleted grant or registration. Regression tests exercise listing followed by deletion and dispatch using the same domain repositories.

The existing coverage thresholds, independent-review gate, test-resource names/region and production merge gate were not weakened. Contracts are not automatically published.

## Observed verification

The authoring container lacks the .NET SDK and could not resolve github.com for cloning. GitHub connector writes and actual GitHub Actions evidence were used instead.

Implementation commit **`7320e082b2d2f1aa5cd48c45c03e49482acbb23f`** passed the complete [Build and local tests job](https://github.com/niobium-nz/Niobium.AI.Agent.Driver/actions/runs/45141602121/job/137580687829) on November 23, 2026:

| Check | Observed result |
| --- | --- |
| Restore and build | Solution and standalone Deployment.Tests project passed. |
| Infrastructure | Both main.bicep and gateway-auth.bicep compiled. |
| Delivery policy | 20 Node tests and 16 Python tests passed. |
| Unit tests | 68 passed, 0 failed, 0 skipped. |
| Actual-adapter and host integration | 3 passed, 0 failed, 0 skipped. Includes real Azurite persistence, service-side stale ETag 412, current grant permission and actual DI/HTTP handler composition. |
| Native MCP wire | Passed initialize, tools/list and both disabled-mode tools/call checks against the real local Functions host. Protocol 2025-06-18; structured object and matching text results. |
| Coverage | 575/615 authored lines, 93.50% combined. Contracts 100%; Core 100%; Server 86.80%. Existing 90% combined / 80% per-module gates passed without authored-file exclusions. |
| Runtime evidence | .NET SDK 10.0.404; Functions Core Tools 4.17.0; Azurite 3.35.0. Resolved package graph captured in the workflow artifact. |
| Azure test deployment | Started automatically after the successful verification job. Consult the exact run/PR for its independently observed outcome; it is not implied by local test success. |
| Real ChatGPT OAuth and upstream live acceptance | Not executed; no authorized live credentials/resources used for agent dispatch. |
| Production activation, merge, package publication, Stage 2 | Not performed. |

Evidence artifact: `local-test-evidence-7320e082b2d2f1aa5cd48c45c03e49482acbb23f` in run `45141602121`. Later documentation-only changes do not extend this evidence to a new head: that head still requires its own delivery checks.

Earlier CI findings were fixed, not bypassed: add the explicit Core Options dependency, pin the resolved Niobium Table adapter, assert the actual Azure RequestFailedException/412 behavior, and share one real host per integration-test process because Niobium modules have process-wide registration guards. The final passing job still reports upstream obsolete MCP schema overload warnings and the public-cloud issuer Bicep warning; these were not suppressed.

## Remaining release gates

Before readiness: require the final exact-head workflow, test deployment, trusted-main health verification and independent review. This PR changes delivery files and therefore requires the existing administrative activation procedure in [the approval guide](pr-approval-handoff.md); a green candidate job alone cannot authenticate its own changed workflow. Keep the PR draft while required verification or review is incomplete.

Before activation: validate actual ChatGPT OAuth discovery/client/redirect settings and delegated claims; prove mandatory authentication at every host path; restrict scaffold CORS; adapt health probes for mandatory authentication; verify platform telemetry does not capture bodies or authorization headers; perform only an authorized harmless nonproduction A-to-B trial; record exact commit/runtime and safe evidence. No Anonymous MCP bypass or enablement is included.

Follow [compatibility evidence](compatibility-evidence.md) and [delivery and operations](delivery-and-operations.md). Local disabled-mode tests do not substitute for authenticated live acceptance.
