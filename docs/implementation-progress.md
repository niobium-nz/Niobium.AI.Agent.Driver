# Agent Gateway implementation progress

## Scope and branch

Implements the Stage 1 source path from the handover on Driver baseline `56c92c72538bdcf7e48abe0a3396b6a54e0e0569`. Stage 2 is not started. Work continues on `feature/agent-gateway-stage1`, superseding the original `codex/agent-gateway-stage1` branch and PR #4. Consult the replacement PR for its exact head and terminal workflow results; older passing evidence does not certify a new commit.

## Implemented source

Contracts/Core/Server layering; native list_agents and dispatch_agent; bounded deterministic handoff encoding; principal-scoped grants; current-row domain authorization; conditional operator writes with full WriteId read-back verification; fixed-origin provider client with bounded same-key retries and uncertainty preservation; independently validated delegated bearer identity; disabled-by-default composition; configuration-only Table provisioning; mandatory-auth deployment template; operator command; unit, Azurite, composition and native MCP tests; development/security/operations guides.

Original binding JSON preserves task numbers, strings and case-distinct keys. Current authorization is read directly inside each domain from its repository so cached snapshots cannot revive a revoked or deleted grant/registration. Regression tests cover these cases.

## Feature delivery correction

All new branches must start with `feature/`. The reviewed workflow already deploys every non-main push to test. The original failure of `Test deployment verified` came from changing the protected pipeline definition, not from a missing deployment trigger.

The correction restores `.github/workflows/azure-deploy.yml` and `azure.yaml` byte-for-byte to their reviewed main versions. The trusted status controller, policy, coverage script and independent deployment harness remain unchanged. Local service setup now belongs to the integration-test assembly, so both the existing CI command and the existing prepackage command execute real persistence and native MCP tests with owned pinned dependencies. No tests or coverage thresholds are skipped or lowered. See [feature branch delivery](feature-branch-delivery.md).

## Addressed review findings from PR #4

- The stable target alias joins workspace, principal, immutable channel and event ID in the idempotency key. Distinct aliases sharing a channel no longer collide.
- Cancellation during authentication before a dispatch flow starts returns deadline_exceeded and same-event retry advice, not an operator configuration error.
- Tenant GUIDs use one lowercase canonical representation for discovery, exact issuer validation and principal derivation.
- Each retry tracks whether that attempt started a send. Cancelling an unstarted retry preserves the previous definitive rate-limit response rather than inventing uncertainty.

New regression tests cover each correction. Native code/security review must be repeated for the replacement head.

## Historical observed evidence

Original head `0125b66e0443d779024ab7383235a02815f3c3ae` passed [run 45142441992](https://github.com/niobium-nz/Niobium.AI.Agent.Driver/actions/runs/45142441992): build, Bicep, 68 unit tests, 3 integration tests, the disabled native wire probe, 93.50% combined coverage, packaging, Azure test deployment and the independent health harness. Its required trusted status failed because its workflow files differed from main. Those results are historical, not proof of the new feature head.

The authoring environment has no .NET SDK and cannot resolve github.com for cloning. Changes are made through the GitHub connector and executable .NET/Azure evidence is obtained from actual GitHub Actions runs. No local authoring-environment .NET pass is claimed.

## Remaining gates

Before merging: successful exact-head build/tests/coverage, test deployment, independent trusted-main verification, the real Test deployment verified status, independent approval and resolved findings. The feature correction does not require accepting a modified candidate workflow as trusted.

Before enabling dispatch: validate actual ChatGPT OAuth discovery/client/redirect settings and claims; prove mandatory authentication at every host path; restrict CORS; adapt health probes; verify telemetry redaction; perform an authorized harmless nonproduction A-to-B trial. Dispatch remains disabled by default. No anonymous MCP bypass, production activation, live agent call or Stage 2 implementation is included.
