# Protected gateway acceptance

The new `scripts/probe_gateway_auth.py` automates the read-only part of the remaining activation gate. It is separate from the currently trusted scaffold health harness and does not silently change CI authentication. Run it only against an explicitly approved TEST origin after the authentication deployment and the current pipeline transition have been reviewed. It cannot register an Entra application, obtain user consent, prove interactive ChatGPT OAuth refresh, or authorize agent actions.

## Operator inputs

Supply an exact deployed commit and one approved HTTPS origin. Credentials are read only from the process environment, not command-line arguments, files in this repository, URLs or output. Obtain the delegated test tokens using the tenant's reviewed process; do not paste tokens into a PR or chat.

- `GATEWAY_TEST_ACCESS_TOKEN`: valid token for the gateway with the required scope and the intended test principal.
- `GATEWAY_EXPIRED_TEST_TOKEN`: deliberately expired test token.
- `GATEWAY_WRONG_AUDIENCE_TEST_TOKEN`: token for a different test resource, not an unrelated production service.
- `GATEWAY_WRONG_SCOPE_TEST_TOKEN`: intended gateway audience but without the dispatch scope.

```sh
python scripts/probe_gateway_auth.py --origin https://APPROVED_TEST_HOST --commit FORTY_CHARACTER_DEPLOYED_SHA
```

The script sends no agent dispatch, tracked start, result submission, route/grant write or storage mutation. It verifies mandatory authentication against missing, malformed and spoofed identity headers; negative delegated tokens; authenticated health and exact deployed commit; native MCP initialization/discovery; and read-only `list_agents` invocation under the validated caller. The public response report contains only check names, booleans, the expected SHA and skipped prerequisite names. It never writes bearer values, response bodies, session identifiers, exception messages or the host URL to the report.

Redirects are not followed and proxies are disabled. A token is sent only to the operator-selected origin, never to discovered URLs. Per-request timeouts and a 64 KiB response bound prevent unbounded waits or captures. The test does not infer permission from a UI conversation or model-supplied agent name. Missing negative tokens produce explicit skipped checks and a nonzero exit; skipped prerequisites are not a pass. Repeat independently for each approved ingress hostname rather than forwarding one origin's token to a discovered host.

## CORS

The reusable Function template now defaults to no allowed browser origins and disables credentialed CORS. Server-to-server calls do not require a browser allowlist. A future browser-origin exception requires explicit reviewed configuration in the generic `allowedCorsOrigins` parameter; do not add `*`. The probe checks that an untrusted synthetic origin is not granted browser access. CORS is not a replacement for OAuth or caller authorization.

## Evidence still requiring the real environment

Record a sanitized actual ChatGPT connection session proving resource/authorization metadata, the actual client ID and redirect URI, consent, delegated issuer/audience/scope, expiry and refresh, and trusted transport context. Prove all direct/custom ingress paths deny spoofed headers and missing credentials. Run the probe against the exact deployed commit. Inspect platform telemetry with synthetic canary values to verify no headers, task/result bodies, capabilities or private provider responses are captured by platform instrumentation.

Only after those checks and approved Table/RBAC/Key Vault inputs, publish harmless test A/B agents with explicitly approved tools and destinations. Test store-only A-to-B-to-result retrieval first; then separately test notification into an operator-bound parent API conversation. Neither this probe nor a passing health endpoint starts those trials. Workspace publication, permission changes, test credentials and authorization for billable handoffs remain operator-owned inputs.

`infra/gateway-auth.bicep` and the new probe provide source support; they are not a claim that the actual OAuth deployment is activated. Production verification remains read-only. The unchanged trusted pipeline's scaffold health check must be adapted in a separately reviewed activation change before mandatory authentication is applied to every path.
