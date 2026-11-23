# Stage 1 Agent Gateway architecture

This implementation follows the [handover](niobium-agent-gateway-handover/Niobium.AgentGateway.Implementation-Plan.md). It retains this repository's `Niobium.AI.Agent.Driver` name. Stage 2 is not started. Deployment does not activate dispatch: both the application default and `infra/main.bicep` set `AgentGatewayOptions__Enabled=false`.

## Boundaries

`Server -> Core -> Contracts`. Contracts contain transport-neutral request/result records and explicit stable wire enums. Core owns configuration entities, rich Niobium domains, validation, canonical serialization, retry classification and coordinating flows. Server owns the native Azure Functions MCP boundary, Entra access-token validation, outbound HTTP, composition and the local operator command.

Only `list_agents` and `dispatch_agent` are native MCP tools at `/runtime/webhooks/mcp`. `NativeGatewayFunctions` is the transport entry point; `GatewayTools` authenticates and translates domain results into `CallToolResult` objects. The result contains a structured object and a matching JSON text block. No HTTP endpoint impersonates MCP, and no JSON-encoded string is passed off as structured content.

The extension's typed argument converter can coerce numbers, dates, GUIDs and case-distinct property names. Dispatch therefore reads the original trigger binding JSON for arguments; the typed context is used for transport identity only. `NativeArgumentsTests` is the regression guard. Runtime wire compatibility remains an explicit gate, not an inference from these tests.

## Current authorization and configuration

`AgentRegistrationDomain` decides whether an alias is enabled and valid. `AgentGrantDomain` checks principal partition, version, permission, enabled state and expiry. Flows use `IDomainRepository`, not Azure SDK clients. The Server explicitly calls `Niobium.Platform.StorageTable.DependencyModule.AddDatabase`.

A Niobium forced single-row lookup can still return a partition-cached domain. Dispatch explicitly reloads the grant and registration before checking permission, avoiding cached authorization after a prior partition listing. There is no authorization cache in the gateway.

The only application tables are `AgentRegistration` and `AgentGrant`. Keys and ETags use Niobium mapping attributes. No input, event fingerprint, handoff, status, run result or recovery queue is persisted. Registry writes are operator-only conditional writes through domains. A fresh WriteId and full read-back equality check detect missing write-response ETags and conflicting post-write snapshots. This does not replace service-side ETag preconditions.

## Submission and retries

The writer rejects unknown request fields, duplicate JSON properties, invalid aliases/event IDs, nonobject data, excessive depth/properties and excessive canonical UTF-8 input size. It sorts object keys ordinally, preserves arrays/text, and emits numeric tokens without floating-point conversion. It also checks the final escaped provider body size.

One deterministic envelope is serialized as the provider's string `input`. The request bytes and idempotency header are frozen for immediate retries. The versioned SHA-256 key uses length-prefixed workspace, validated principal, immutable channel and event identity. Conversation keys are separately namespaced by workspace/principal/target and require both global and per-registration permission.

The sole outbound origin is `https://api.chatgpt.com`. The client does not follow redirects, and its registered pipeline removes additional retry handlers while preserving the platform primary transport. It uses response-headers completion and never reads optional response metadata. A known 202 is acceptance even if the body is empty, malformed or fails later. `conversation_url` is deliberately omitted in this version.

There are at most three attempts, eight seconds per attempt and a linked 25-second end-to-end budget including authentication and configuration reads. 429 respects Retry-After if it fits. 408, 5xx and post-send network/cancellation errors preserve uncertainty. A later rejection cannot erase earlier uncertainty; a subsequent 202 can establish acceptance.

Acceptance is not completion. Stage 1 provides neither exactly-once execution nor permanent deduplication. It cannot detect a later reuse of an event ID with changed input. Callers must preserve the target, handoff ID and complete content for retries.
