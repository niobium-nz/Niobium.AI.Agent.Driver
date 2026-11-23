# Agent Gateway implementation progress

## Scope and baseline

Implements the Stage 1 source path from the handover on Driver baseline `56c92c72538bdcf7e48abe0a3396b6a54e0e0569`. Stage 2 is explicitly not started. The change is for PR review, not production activation or merge. The PR head and each workflow artifact name identify the immutable candidate SHA; never transfer a passing result from an older head.

## Implemented source

Contracts/Core/Server layering; native list_agents and dispatch_agent; bounded deterministic handoff encoding; principal-scoped grants; explicit current-row reload; conditional operator writes with full WriteId read-back verification; fixed-origin provider client with bounded same-key retries and uncertainty preservation; independently validated delegated bearer identity; disabled-by-default composition; configuration-only Table provisioning; mandatory-auth deployment template; operator script; unit, Azurite, composition and native MCP wire tests; development/security/agent/operations runbooks.

The raw MCP binding JSON is used for dispatch arguments to avoid the extension's typed argument coercion. A current-row ReloadAsync is used in addition to forceLoad because Niobium's partition cache can otherwise return a stale domain. Both findings have regression tests.

The existing coverage thresholds, independent-review gate, test-resource names/region and production merge gate were not weakened. Contracts are not automatically published.

## Verification status at authoring

| Check | Result |
| --- | --- |
| Repository and handover inspection through connected GitHub | Completed; implementation based on the referenced baseline |
| `git clone --depth 1 https://github.com/niobium-nz/Niobium.AI.Agent.Driver.git ...` | Blocked: could not resolve github.com in the execution container |
| Local .NET restore/build/test | Blocked: no dotnet SDK installed in this execution container |
| Local Bicep compilation and coverage | Not executed; no passing result claimed |
| GitHub CI for the candidate | Inspect the exact-head workflow/check results recorded on the PR; not assumed successful |
| Real ChatGPT OAuth and upstream live acceptance | Not executed; no authorized live credentials/resources used |
| Production activation, merge, package publication, Stage 2 | Not performed |

## Remaining release gates

Before readiness: get a clean package graph/build, resolve any exact-head CI failures, pass the existing 90% overall/80% per-module coverage gates, real Azurite and native wire tests, and the repository's independent review policy. Keep this PR draft while verification is blocked or incomplete.

Before activation: validate actual ChatGPT OAuth discovery/client/redirect settings and delegated claims; prove mandatory authentication at every host path; remove the scaffold wildcard CORS policy or replace it with a reviewed restrictive policy; adapt health probes for mandatory authentication; verify platform telemetry does not capture bodies or authorization headers; perform only an authorized harmless nonproduction A-to-B acceptance trial; record its exact commit/runtime and safe evidence. No Anonymous MCP bypass or enablement is included in this implementation.

These are explicit acceptance blockers, not completed work. Follow [compatibility evidence](compatibility-evidence.md) and [delivery and operations](delivery-and-operations.md) rather than inventing missing runtime results.
