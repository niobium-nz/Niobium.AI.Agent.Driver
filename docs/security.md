# Agent Gateway security boundary

## Identities and trust

Separate incoming Entra principal/client, Function managed identity, Workspace Agent token owner/scope and job-capability signing identities. No incoming bearer is forwarded upstream. Credentials never belong in tool outputs, general-purpose registry/job rows, exception text or logs. Only B's task envelope receives its completion capability; parent notifications contain only bounded IDs/hash metadata.

A shared OAuth connection cannot distinguish exact Workspace Agent runtimes. Distinct privileges require independently distinguishable approved connections in the actual builder. OAuth plus a job capability plus current grants proves permission and possession, not agent-runtime attestation.

`McpCallerContextAdapter` validates bearer signatures and exact single-tenant issuer/audience/lifetime/client/scope. Tenant IDs are canonicalized. It ignores claimed X-MS-CLIENT-PRINCIPAL headers and accepts only the supported Streamable HTTP context. Model-supplied callers, arbitrary URLs, channel IDs, tokens and raw parent-conversation bindings are not authority.

## Deployment ordering

Ordinary IaC disables dispatch, tracked features, both optional follow-ups and the maintenance timer. No change here sets the native webhook level to Anonymous. The separate mandatory App Service OAuth template protects execution paths with HTTPS and unauthenticated 401, without guessed client redirects.

Before activation, prove actual ChatGPT discovery, client registration, redirect, consent and refresh. Test every raw/custom ingress with missing/spoofed credentials, invalid/expired/wrong-audience tokens, missing scope and unauthorized clients. When native system-key authentication remains active, include a valid system key in a separately authorized negative OAuth trial so a key-only 401 is not mistaken for OAuth rejection. The read-only probe is for the reviewed OAuth-facing deployment; it deliberately cannot pass its positive checks when an unsupplied native key is still required.

Only a separately reviewed activation supported by that evidence may change native key requirements. Default CORS is now an empty browser allowlist with credentials disabled; exceptions require explicit reviewed origins, never wildcards. CORS does not replace authentication. The unchanged trusted scaffold health harness must transition to authenticated probes before mandatory OAuth covers all paths. The new read-only probe does not itself change trusted CI policy.

## Current authorization and routing

Stage 1 dispatch requires a current enabled registration and dispatch grant. New tracked jobs need explicit version-2 start permission. Completion binds persisted workspace, job, target, writer, operation, nonce, expiry and signing-key version and rechecks current completion grants/binding. Reads remain initiating-principal-only; opaque IDs do not grant access. No new permission is inferred from version-1 grants.

Registration channel/credential and version-2 schema/writer bindings are immutable. Operator continuation routes also have immutable owner, destination and actual API conversation. Parent notifications require a current enabled/unexpired matching route, the owner's current parent dispatch grant, matching enabled registration and both global/registration reuse permission. Route/grant revocation holds an unsent notification; it cannot undo an already accepted external action.

Outbound routes are fixed at the Workspace Agents origin, with validated channel/run identifiers and redirects disabled. Polling is separately opt-in and read-only, with bounded body/time/rate policy and current authorization. Optional POST run metadata cannot change known acceptance or cause a duplicate send. Provider completion is observation only and does not assert result availability or user approval.

## Capabilities, concurrency and retention

Use a dedicated stable HMAC-SHA256 identity key and an independent versioned HS256 capability key ring. Canonical base64 secrets are projected via the existing Key Vault path. Synthetic test keys are not production defaults. Verification has a fixed algorithm/issuer/audience and requires signature validity plus canonical equality with frozen job claims. Token-supplied keys, issuers or algorithms never select an alternative authority.

Retain old signing versions through outstanding capability expiry. Do not rotate the identity key in place while tombstones exist; migration/compatibility lookup is a separate task. Disable new starts independently from access for issued callbacks/results. Global disable is an emergency full stop, not a graceful rollout.

Conditional claims use ETags, owner, expiry and generation plus complete WriteId read-back. Result and pending parent notification commit atomically in one entity. An unrelated writer's ETag cannot finish an old claim. Lease expiry never proves an external request was not accepted; Unknown/Held work is not automatically resent after recovery. The first valid result is immutable and duplicates cannot reset the notification outbox.

Input/result JSON is sensitive. Canonical UTF-8, per-property UTF-16 and aggregate limits apply. Unknown/duplicate fields, unsupported schemas and unknown state codes fail closed. Stored content remains untrusted data, not a command to change permissions. Cleanup removes payload properties through replacement only after retention AND capability expiry; unresolved notification or initial-dispatch uncertainty prevents automatic purge. Tombstones preserve deduplication. Retention is not proof of remote cancellation or upstream deletion.

## Telemetry and remaining limits

Application dispatch logs contain only opaque event digest, public alias, outcome, attempt count and duration. Metadata parse warnings contain only the opaque event digest. Maintenance reports aggregate counters. The authentication probe reports check names/booleans and expected commit, not tokens, hosts, session IDs, responses or private exception details. HTTP body/header logging remains disabled for the typed client.

These source controls do not prove Azure host/platform instrumentation is private. Before activation, inspect actual telemetry with synthetic canaries and verify no task/result bodies, bearer tokens, capabilities or private provider content is captured. No live OAuth or agent acceptance result is claimed from disabled deployment health.

There is no model-controlled callback URL, generic agent resume, arbitrary ChatGPT UI-thread injection or additional-reader policy. External exactly-once execution and automatic ambiguous-job resolution are not guaranteed. Keep the upgraded reader/callback binary for version-2 jobs even when optional flags are rolled back. See [job follow-ups](job-follow-ups.md) and [protected acceptance](protected-gateway-acceptance.md).
