# Feature branch delivery

## Branch and environment contract

Use `feature/<description>`, including for follow-up fixes. The Agent Gateway implementation continues on `feature/agent-gateway-stage1`; its replacement PR links the original PR #4 so review history and earlier evidence remain available.

The reviewed Azure Deployment workflow already uses `push.branches-ignore: [main]`, with no path filters. Consequently every feature branch push, including nested names and documentation-only commits, starts verification and deployment to `test` in `rg-niobium-agents-test`. This broad existing trigger is retained for compatibility; it is not a reason to create non-feature implementation branches. A merged PR to `main`, not a feature push, selects production.

## Earning Test deployment verified

The privileged readiness workflow executes from immutable `main`. It verifies the exact pushed SHA, the expected repository/workflow, successful build/local checks, successful test deployment, deployed commit verification, candidate integration, and the independent read-only harness from the same trusted main revision. Only then does that controller publish `Test deployment verified`. Branch naming or a green build alone is not sufficient.

PR #4 changed `.github/workflows/azure-deploy.yml` and `azure.yaml`. That caused the unchanged main controller's pipeline-identity check to reject it even after deployment passed. The feature-branch correction restores both files to their reviewed main blobs. `scripts/check-coverage.py`, `scripts/test-resources.py`, `scripts/delivery-policy.cjs` and the readiness workflow are unchanged. There is no fabricated status, weakened check, protection bypass or direct main write.

## Self-contained integration dependencies

`IntegrationHostFixture` now owns its local services in the standard integration assembly. This makes the unchanged CI integration command and the unchanged azd prepackage `dotnet test --solution` command both work without assuming that another runner left Azurite running.

The fixture obtains Azurite 3.35.0 through npm, rejects occupied local ports, uses a unique temporary data directory and stops only its own process tree. The native MCP test obtains Functions Core Tools 4.17.0, copies the already-built Server output into a disposable host directory, starts a disabled loopback host, and executes the existing real wire probe. It does not rebuild instrumented assemblies, reuse an unrelated emulator, invoke Azure storage mutation tests, or send work to a live agent. The integration suite also compiles the standalone mandatory-authentication Bicep template.

Prerequisites are .NET 10, Node.js/npm and Azure CLI/Bicep in Linux, macOS or WSL. Pinned npm runtimes are cached normally; the first run needs package-download access. Ports 10000-10002 and 7071 must be free. Logs are retained under `artifacts/compatibility/<run-id>/`. There are no successful skips when dependencies fail to start.

```sh
dotnet test --solution Niobium.AI.Agent.Driver.slnx --configuration Release
# Integration-only entry point, including coverage:
bash scripts/verify-gateway-local.sh
```

The existing 90% combined and 80% per-module authored-code coverage gates remain unchanged. `scripts/test_feature_delivery.py` protects feature-push inclusion, test-environment selection and rejection of unsuccessful or mismatched run evidence.

## Review and activation remain separate

Keep independent current-head approval and resolved review threads. Changes to trusted pipeline definitions still require the existing reviewed activation procedure; this application PR no longer changes them. Merging this disabled implementation is not permission to enable live dispatch. OAuth, ingress, telemetry and authorized nonproduction agent acceptance remain activation prerequisites.
