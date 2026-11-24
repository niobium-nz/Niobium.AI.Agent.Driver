# Live gateway acceptance runbook

Follow the gates in order on TEST. Use the [owner configuration guide](live-owner-configuration.md), record results in [live acceptance evidence](live-acceptance-evidence.md), and use the [activation and rollback procedure](live-activation-and-rollback.md) for setting changes. Coordinate the test deployment window and any existing agent trials before pushing. [PR review and approval](pr-approval-handoff.md)

## Before a live session

- Record the approved subscription, TEST app and ARM-resolved default host, every other approved ingress, exact deployed SHA, reviewed GitHub run, and operator. Check that no feature deployment or conflicting trial is active. Verify the health commit again before and after each live test group.
- Inventory actual authentication, CORS, native webhook authorization, execution flags, timer binding, host and application storage, identity roles, Key Vault reference resolution, and outstanding jobs. Keep nonpublic identity and routing evidence in the restricted operator record. Use safe labels and boolean outcomes in the shared record.
- Record the scope, rollback owner, change window, maximum logical scenarios, total trigger attempts, cost ceiling, permitted same-event retries and stop conditions. Include initial A execution, B attempts and later A notification separately. Account for timer work already queued before enabling maintenance.
- Load short-lived credentials from the approved secret mechanism into the test process environment. Preserve the separation between delegated caller, app-only health verifier, runtime managed identity, upstream Workspace Agent API token, and job signing keys. [Security boundary](security.md)

## Gate 1 - OAuth and authenticated health

Resolve the actual connection details from the approved ChatGPT workspace and Entra tenant. Capture the canonical MCP resource, protected resource and authorization-server metadata, the unauthenticated OAuth challenge, registration mode, actual client ID and redirect, code flow with PKCE, consent, token audience and delegated scope, and a real refresh or reconnect. Preserve the gateway's exact issuer and audience, delegated claims, and allowed-client checks. Entra requires a preconfigured client where dynamic registration is unavailable. Azure App Service protected resource metadata support is in preview. [ChatGPT OAuth](https://developers.openai.com/plugins/build/auth) · [Azure built-in MCP authorization](https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-mcp)

Plan the protected health transition with the repository maintainer before turning on mandatory authentication. Use a dedicated approved health-verifier identity with short-lived credentials authorized only for read-only health. For Entra app-only access, verify the app registration's assignment and the chosen enforcement of its health authorization. Keep the application's allowed MCP callers delegated and unchanged. Verify the app-only bearer is rejected at the MCP tool boundary even with a valid native key during the dual-gate phase. [Entra scope and app role verification](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-protected-web-api-verification-scope-app-roles)

The opt-in health command checks that the authorized GET returns ready and the exact SHA, and that a GET without a bearer is denied. Supply `READONLY_API_ACCESS_TOKEN` through the trusted runner's approved identity. Run from a runner with direct HTTPS access to the approved host.

```sh
READONLY_API_URL="$APPROVED_TEST_ORIGIN" \
READONLY_EXPECTED_COMMIT="$EXPECTED_DEPLOYED_SHA" \
python scripts/read-only-deployment.py --require-authentication --report artifacts/protected-health.json
```

Review the workflow and main-pinned harness together. Stage the reviewed credential acquisition, verifier and immutable references through the maintainer's trust-transition procedure. Keep the independent runner and exact commit check, and use an approved protected TEST acceptance window. Once activated, require authentication on both candidate and independent health paths and verify the trusted status on a subsequent exact feature head. A workflow that the current controller cannot authenticate needs the documented administrator decision before activation. [Deployment workflow changes](pr-approval-handoff.md#deployment-workflow-changes)

## Gate 2 - Ingress and caller acceptance

Apply approved App Service authentication while execution remains disabled and retain the native webhook key. A reviewed public metadata path may be needed for discovery. Confirm the exact path is public and verify every approved execution and health ingress is protected. Use the read-only probe with a secured native MCP key and delegated and app-only test tokens. Inspect platform and application evidence to locate each rejection layer. [Protected gateway probe](protected-gateway-acceptance.md)

For positive caller testing, use the approved registry-only window with a valid configured credential alias, no dispatchable registration and grant combination, tracked access and starts off, and the timer off. Confirm the actual registry state first. Verify both authentication gates and all six advertised native tools and call only `list_agents`. Check that every MCP call still requires its own bearer even if a session ID is reused. Verify A and B through their distinct validated issuer, tenant, object and client tuples in a restricted record. Connector names and successful empty lists cannot establish that the principals differ.

An observed client that cannot supply the native key needs a separately reviewed decision after the dual-gate ingress proof. Preserve mandatory OAuth on every raw and custom host during any switch. Repeat the protected ingress, real ChatGPT login, consent, listing and refresh checks against the resulting endpoint before allowing a dispatch grant.

## Gate 3 - Storage, credentials and explicit permissions

Use the reviewed application Table endpoint and runtime/operator roles. Keep the host storage account separate. Use `infra/tracked-jobs.bicepparam` to stage the job and maintenance tables for store-only or polling. Use `infra/parent-continuation.bicepparam` when the parent route has a separate approval. Verify parameters and the deployment diff before applying either option.

Use the existing Key Vault projection for the approved upstream credential alias, independent stable job identity key and versioned signing keys. Confirm reference resolution. Retain previous signing versions through outstanding expiry and preserve the stable identity key while deduplication tombstones exist.

Provision initially disabled version-2 B registrations and grants through the [registry command](configuration-and-development.md#operator-registry-command). Derive partitions and writer keys from validated claims using the code's canonicalization. Give A explicit `CanStartJob` and B explicit `CanSubmitResult` for the approved alias. Keep `CanDispatch` separate. For updates, use the fresh `expected_e_tag` and verify the returned WriteId and intended stored snapshot. Stop on a conflict and reconcile. [Tracked jobs](tracked-jobs.md)

## Gate 4 - Store-only A/B exchange

Publish only the approved synthetic A/B revisions with explicitly reviewed tools and connections. A requests `start_agent_job` with `agent-result.v1`, `completion_mode` set to `store_only`, and one stable handoff ID and payload. Use input values 6 and 7 with an approved non-secret acceptance marker. B computes 42, includes the marker in the bounded result data, and submits it with its authorized connection and the job capability. A uses its original initiating connection to retrieve the stored result.

Keep polling, parent continuation and maintenance off during the initial store-only trial. Verify the actual A and B tool behavior and exact stored state. Exercise duplicate start, changed-content conflict, duplicate and conflicting callbacks, unauthorized reader and writer denial, and callback/read behavior after pausing starts within the approved matrix. Use isolated fixtures for other negative and race cases. Use a stable event and identical content for an approved retry of the same logical work.

## Gates 5 and 6 - Separate follow-up trials

For **provider observation**, inventory existing jobs before enabling the timer and polling. Record the observed run ID association privately, time bounds, GET budget and finite state progression. Verify that provider completion leaves the business result unchanged. Record absent beta run metadata as a limitation and preserve the original send outcome. [Workspace Agent trigger and run reference](https://developers.openai.com/workspace-agents/trigger-runs)

For **parent continuation**, provision an enabled, expiring operator route for the exact approved A API conversation. Verify the frozen and current route binding, parent registration, `CanDispatch` grant and both reuse permissions. Bootstrap A in that same API conversation and verify its actual key privately. Use a separate `notify_parent` job bound to the reviewed reference. Verify that the result and pending notification were saved together, the timer submitted one result-ready event, and the exact parent conversation received it and read the matching authorized result. Count the parent submission separately. [Optional follow-ups](job-follow-ups.md)

## Gate 7 - Platform privacy and quiescence

Audit the worker, Functions host, App Service diagnostics, request/dependency telemetry, proxies and exports before allowing traffic and again after the synthetic trial. Use restricted telemetry access and an approved TEST time window. Verify ingestion with a safe positive control and account for sampling and delay. Search for a non-secret synthetic marker using aggregate counts. Check the permitted surfaces for authorization/capability/header capture and task, result or provider-body capture. Keep raw events and private claims in the approved restricted record.

Pause starts and both optional follow-ups, inventory outstanding jobs and notifications, and verify the [rollback procedure](live-activation-and-rollback.md). Assign unresolved sends to the operator and preserve snapshots, deduplication tombstones and keys. Report separate TEST and production effective states. A production activation proposal depends on complete TEST evidence and a separate owner decision.
