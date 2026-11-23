# Agent Gateway security boundary

## Identities and trust

Separate identities exist for the incoming ChatGPT-connected Entra principal/client, the Function App managed identity, the owner/scope of the server-held Workspace Agent token and the job-capability signing service. No incoming bearer is forwarded upstream. Raw tokens never belong in tool outputs, registry rows, diagnostic exception text or logs. Only B's approved trigger envelope contains its completion capability.

The gateway cannot distinguish exact Workspace Agent names when agents share one authenticated connection. Distinct privileges require independently distinguishable connections verified in the actual builder. A capability plus OAuth and a current grant proves possession and permission within that boundary, not an independently attested agent-runtime identity.

`McpCallerContextAdapter` validates the bearer cryptographically against single-tenant discovery/JWKS and exact issuer/audience/lifetime/client/scope requirements. It ignores claimed X-MS-CLIENT-PRINCIPAL headers. Tenant GUIDs are canonicalized for discovery, issuer and principal derivation. Only Streamable HTTP transport is accepted. Model-supplied caller/workspace/channel/token/arbitrary callback routing is not accepted.

## Deployment ordering

Ordinary IaC disables the gateway, tracked features and maintenance timer. This change never sets the native MCP webhook authorization level to Anonymous. The separate mandatory App Service OAuth template uses HTTPS and unauthenticated 401 responses, without guessed ChatGPT redirects or excluded execution paths.

Before activation, prove actual OAuth discovery, client enrollment, redirect and refresh behavior. Test the raw Azure hostname and every reachable path without a bearer, with spoofed platform headers, invalid/expired/wrong-audience tokens, missing scope and unauthorized clients/principals. Before a native key bypass is considered, include a valid MCP system key in negative OAuth tests so a key-authentication 401 is not mistaken for proof of OAuth.

Only a separately reviewed activation backed by that evidence may change webhook key authentication. The same activation must restrict scaffold CORS, authenticate health/deployment probes and verify that host/platform telemetry excludes bodies, authorization headers and completion capabilities. The public scaffold health endpoint is deployment metadata, not evidence of an authenticated MCP connection.

## Current authorization and outbound isolation

Stage 1 dispatch requires current enabled registration and dispatch grant. New tracked work requires version-2 registrations and explicit job-start permission. A completion must match the persisted workspace, target, job, writer, operation, nonce, expiry and signing-key version, and must pass current registration/grant checks. Version-1 grants confer no tracked permissions. Reads are initiator-only; knowing an opaque job ID is insufficient.

Registration channel and credential bindings are immutable. Version-2 result schema and completion writer are also immutable; rebindings need a new alias. Domains read current repository rows so deleted or revoked rows cannot revive through cached snapshots. ETag preconditions remain enforced by the storage service; read-back comparison is additional reconciliation, not a substitute.

The provider origin and route shape are fixed, redirects are disabled, and no tool accepts arbitrary endpoints or headers. The typed client has one retry owner. Application logs contain only opaque event digest, public target alias, outcome, attempt count and duration; maintenance logs only aggregate counters. SDK/host telemetry still needs separate live privacy verification.

## Job capability, data and retention

Job IDs use a dedicated HMAC-SHA256 key. Completion uses standard HS256 JWTs with a separate versioned key ring and fixed issuer/audience. Strong canonical base64 key material is configured through the existing Key Vault path; tests use explicitly synthetic keys. Verification allows only HS256 and requires both signature validity and equality to the canonical token reconstructed from immutable stored claims. It never trusts a token-supplied key version for routing to a different job or authorizer.

Do not rotate the identity key in place while tombstones exist: that changes logical job identities and defeats deduplication. Signing-key rotation changes the active version for new jobs but must retain old versions through outstanding capability expiry. Disabling new starts does not revoke issued callbacks; the independent access flag and current completion grants control access. An emergency global disable also stops completion and retrieval.

Requests/results are sensitive durable data and must only be enabled for an approved storage account, retention policy and data classification. Canonical UTF-8 limits and conservative UTF-16 per-property/aggregate limits are enforced before persistence. Unknown fields, duplicate properties, unsupported result schemas and nonfinite enum codes fail closed. Success data and business-failure messages are untrusted application content, not instructions to a future agent or permission to broaden its privileges.

The first valid result is immutable; an identical callback returns the original receipt and a different result conflicts. Physical replacement removes input/result properties at retention expiry while retaining deduplication tombstones. Unknown/Held jobs are not automatically purged or resent. Retention is not proof that the remote agent stopped executing, nor a mechanism for deleting upstream copies.

## Known boundaries

Acceptance is not completion and a stored result is not automatic approval of downstream actions. Stage 1 has no durable ledger. Stage 2 store-only jobs add gateway deduplication and result exchange but cannot guarantee exactly-once external execution or resolve every crash around an HTTP send. A lease fences storage writes, not external effects. There is no provider polling, callback URL supplied by a model, automatic parent continuation, arbitrary conversation-thread resume or additional-reader sharing policy in this release.

See [tracked jobs](tracked-jobs.md) for state transitions, callback instructions, rollback and the remaining live acceptance requirements.
