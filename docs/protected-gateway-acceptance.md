# Protected gateway acceptance

`scripts/probe_gateway_auth.py` checks protected ingress and read-only native MCP behavior. Run it against an explicitly approved TEST origin in the protected acceptance window. Use the [live acceptance runbook](live-acceptance-runbook.md) for the separate interactive OAuth and trusted health transition.

## Operator inputs

Supply an exact deployed commit and one approved HTTPS origin. Credentials are read only from the process environment, not command-line arguments, files in this repository, URLs or output. Obtain the delegated test tokens using the tenant's reviewed process; do not paste tokens into a PR or chat.

- `GATEWAY_TEST_ACCESS_TOKEN` supplies a valid delegated token for the intended TEST principal with the required scope.
- `GATEWAY_TEST_HEALTH_ACCESS_TOKEN` supplies the app-only verifier token used to prove authorized health and denial of MCP tool use.
- `GATEWAY_TEST_MCP_SYSTEM_KEY` supplies the valid native key during the dual-gate phase. It is sent in `x-functions-key` only on MCP POSTs. Omit it after a separately approved OAuth-only transition.
- `GATEWAY_EXPIRED_TEST_TOKEN` must be past expiration plus the gateway's clock skew.
- `GATEWAY_WRONG_AUDIENCE_TEST_TOKEN` must target a different approved TEST resource.
- `GATEWAY_WRONG_SCOPE_TEST_TOKEN` must target the gateway without the required scope.
- `GATEWAY_WRONG_TENANT_TEST_TOKEN` and `GATEWAY_UNAUTHORIZED_CLIENT_TEST_TOKEN` must isolate their respective restrictions using the approved test identities.

Validate the provenance of each test token with the issuing operator. An invalid string cannot establish a specific audience, tenant, client, scope or lifetime failure.

```sh
python scripts/probe_gateway_auth.py --origin https://APPROVED_TEST_HOST --commit FORTY_CHARACTER_DEPLOYED_SHA
```

The script invokes only the read-only `list_agents` tool. It checks missing, malformed and spoofed authentication, the delegated token matrix, health-only identity isolation, exact-commit health, native initialization, and discovery of all six tool schemas. It verifies the listing's text and structured result match and that an initialized MCP call still needs a bearer. In dual-gate mode, the valid MCP key accompanies OAuth negatives and a separate valid-bearer request must be rejected without the key. Confirm the platform or application rejection layer in restricted evidence. Report output includes check names, booleans, the expected SHA and skipped prerequisite names. Keep bearer and key values, response bodies, session IDs, exceptions and the host URL out of shared evidence.

Use a runner with direct HTTPS access to the selected origin. The probe disables redirects and proxies, bounds each request and limits its response to 64 KiB. It sends credentials only to that origin. A missing negative token or verifier credential produces an explicit skipped check and nonzero exit. Repeat independently for each approved ingress.

## CORS

The reusable Function template now defaults to no allowed browser origins and disables credentialed CORS. Server-to-server calls do not require a browser allowlist. A future browser-origin exception requires explicit reviewed configuration in the generic `allowedCorsOrigins` parameter; do not add `*`. The probe checks that an untrusted synthetic origin is not granted browser access. CORS is not a replacement for OAuth or caller authorization.

## Evidence still requiring the real environment

Record a sanitized actual ChatGPT connection session proving resource/authorization metadata, the actual client ID and redirect URI, consent, delegated issuer/audience/scope, expiry and refresh, and trusted transport context. Prove all direct/custom ingress paths deny spoofed headers and missing credentials. Run the probe against the exact deployed commit. Inspect platform telemetry with synthetic canary values to verify no headers, task/result bodies, capabilities or private provider responses are captured by platform instrumentation.

Only after those checks and approved Table/RBAC/Key Vault inputs, publish harmless test A/B agents with explicitly approved tools and destinations. Test store-only A-to-B-to-result retrieval first; then separately test notification into an operator-bound parent API conversation. Neither this probe nor a passing health endpoint starts those trials. Workspace publication, permission changes, test credentials and authorization for billable handoffs remain operator-owned inputs.

Production verification remains GET-only. The opt-in `read-only-deployment.py --require-authentication` mode checks both authorized exact-commit health and anonymous denial. The trusted pipeline currently invokes scaffold mode. Coordinate the reviewed workflow and identity transition before mandatory authentication covers every path. [Live acceptance runbook](live-acceptance-runbook.md#gate-1---oauth-and-authenticated-health)
