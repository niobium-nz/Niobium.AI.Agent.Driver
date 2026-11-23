# Agent Gateway implementation progress

## Scope and baseline

Implements the Stage 1 source path from the handover on Driver baseline `56c92c72538bdcf7e48abe0a3396b6a54e0e0569`. Stage 2 is not started. The change is for PR review, not production activation or merge. PR #4 and its exact-head workflow artifacts identify the candidate; never transfer a passing result from an older head.

## Implemented source

Contracts/Core/Server layering; native list_agents and dispatch_agent; bounded deterministic handoff encoding; principal-scoped grants; current-row domain authorization; conditional operator writes with full WriteId read-back verification; fixed-origin provider client with bounded same-key retries and uncertainty preservation; independently validated delegated bearer identity; disabled-by-default composition; configuration-only Table provisioning; mandatory-auth deployment template; operator script; unit, Azurite, composition and native MCP wire tests; development/security/agent/operations runbooks.

The raw MCP binding JSON is used for dispatch arguments to avoid typed argument coercion. Current authorization is read directly by each domain from its own repository, including after explicit reload: Niobium's partition cache and entity-initialization fallback must not revive a deleted grant or registration. Regression tests exercise listing followed by deletion and dispatch using the same domain repositories.

The existing coverage thresholds, independent-review gate, test-resource names/region and production merge gate were not weakened. Contracts are not automatically published.

## Observed verification

The authoring container lacks the .NET SDK and could not resolve github.com for cloning. GitHub connector writes and actual GitHub Actions evidence were used instead.

| Candidate / run | Observed result |
| --- | --- |
| Initial `6edfc56` / Azure run 45140693659 | Restore succeeded. Build found a missing Core Options reference; corrected in be3c831. |
| `be3c831` / Azure run 45141138876 | Restore, solution/deployment-test builds, both Bicep compilations, 20 Node policy tests, 16 Python policy tests and all 67 unit tests passed. |
| Same run, real integration tests | Found two issues: stale Table ETag correctly throws Azure RequestFailedException with status 412 (test expected another type); a second host in the same process bypassed Niobium's static registration guards. This follow-up corrects the expectation and shares one real host per test process. |
| Current PR head | Must pass its own workflow. See PR #4 for subsequently observed results; previous-head evidence is not certification of this head. |
| Real ChatGPT OAuth and upstream live acceptance | Not executed; no authorized live credentials/resources used. |
| Production activation, merge, package publication, Stage 2 | Not performed. |

## Remaining release gates

Before readiness: resolve any exact-head CI failures; pass the existing 90% combined/80% per-module coverage gates, real Azurite and native wire tests, test deployment and independent review. Keep this PR draft while verification is incomplete.

Before activation: validate actual ChatGPT OAuth discovery/client/redirect settings and delegated claims; prove mandatory authentication at every host path; restrict scaffold CORS; adapt health probes for mandatory authentication; verify platform telemetry does not capture bodies or authorization headers; perform only an authorized harmless nonproduction A-to-B trial; record exact commit/runtime and safe evidence. No Anonymous MCP bypass or enablement is included.

Follow [compatibility evidence](compatibility-evidence.md) and [delivery and operations](delivery-and-operations.md). Authored tests do not substitute for observed runtime acceptance.
