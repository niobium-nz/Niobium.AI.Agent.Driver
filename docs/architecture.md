# Agent Gateway architecture

The [handover](niobium-agent-gateway-handover/Niobium.AgentGateway.Implementation-Plan.md) separates persistence-free Stage 1 from opt-in durable Stage 2. Stage 1 was merged in PR #5; PR #6 adds tracked results plus separately disabled provider observation and allowlisted parent continuation. Source deployment neither proves live OAuth compatibility nor authorizes external agent execution.

## Boundaries and transport

`Server -> Core -> Contracts`. Contracts contain public wire records and finite enums. Core owns entities, rich Niobium domains, canonical serialization, identity, validation, state transitions and coordinating flows. Server owns native Azure Functions MCP, delegated token validation, signed capability implementation, HTTP, timer entry points and composition. Azure SDK clients occur in framework adapters and test fixtures, not business flows.

At `/runtime/webhooks/mcp`, `list_agents` and `dispatch_agent` keep their Stage 1 contracts. `start_agent_job`, `submit_job_result`, `get_job_status` and `get_job_result` are additive and disabled by default. Thin triggers call boundary services; structured responses match their JSON text. Dispatch/start/submit remain consequential tools. No ordinary HTTP route impersonates MCP.

The extension's typed converter can coerce numbers, dates/GUIDs and case-distinct keys. Model data therefore comes from original binding JSON; the typed transport supplies validated identity only. Native host tests discover all six tools with execution disabled. The read-only protected-endpoint probe additionally supports authenticated initialization/discovery/caller checks once an operator supplies real prerequisites.

## Authorization and persistence

Registration and grant domains check current repository rows, not cached initialized snapshots. Version-1 configuration is dispatch-only; version-2 registrations add immutable result schema/writer, and grants explicitly separate CanDispatch, CanStartJob and CanSubmitResult. Only the validated initiating connection can read its results. A completion also needs the exact job capability and current writer grant.

`McpCallerContextAdapter` validates single-tenant issuer, audience, lifetime, delegated scope and allowed client. Model-supplied caller/workspace/agent identity and spoofed platform headers are not authorization evidence. OAuth, managed identity, upstream tokens and job-capability keys are separate credentials.

The Server explicitly composes `Niobium.Platform.StorageTable`. Normal infrastructure stages only AgentRegistration/AgentGrant. Tracked and parent-route storage require separate parameter files. Operator-only configuration uses current ETags and full WriteId read-back verification. No model-facing route/grant management exists.

## Stage 1 and tracked dispatch

Stage 1 validates canonical input/body byte limits, IDs, unknown fields, duplicates, depth and property counts. It sorts object keys while preserving array order/text/numeric tokens. It owns bounded immediate retries with frozen bytes and stable idempotency keys. The typed transport has a fixed provider origin, disabled redirects and no inherited retry/logging handlers. A known 202 is acceptance regardless of optional response metadata. Stage 1 does not read that metadata or write a job ledger.

Tracked starts commit an immutable request before submission. HMAC job identity binds workspace, initiating principal, target alias and handoff ID; a separate fingerprint detects changed content/settings. Scalar AgentJob snapshots carry independent dispatch/result/provider/continuation states. Claims record owner/generation/first attempt/lease. Every conditional replacement verifies the complete intended WriteId snapshot, excluding only ETag. A competing writer's ETag is not evidence of ownership.

Callbacks store an immutable result. Identical retries receive its original receipt; different results conflict. Acknowledgment and observation writes reload current state so callback races preserve both facts. Available results suppress redispatch regardless of an uncertain earlier submission. The raw completion capability is reconstructed from immutable claims/versioned keys and sent only to B, not persisted or returned to A.

## Optional observation and continuation

`IWorkspaceAgentRunClient` extends the existing typed provider client. Only an opted-in tracked dispatch requests beta run metadata. Optional metadata reads are bounded and occur after acceptance; failure cannot trigger another POST. The worker makes a bounded GET for a known matching run ID. Known statuses map to finite observation codes; unknown statuses never authorize another send. Completed does not mean ResultAvailable.

`AgentContinuationRouteDomain` holds operator-approved immutable owner/alias/channel/credential/API-conversation bindings. The start request supplies only a reference. Current route, grant and registration/reuse policy are checked before delivery. Binding snapshots are persisted with the job so a later configuration change cannot redirect an old notification.

First completion stores the result and Pending notification together in the same conditional AgentJob write. The timer later claims that outbox and submits a deterministic result-ready event, using a stable key distinct from B's original event. The parent reads the authorized stored result. An unknown response or expired Sending lease is held, never automatically replayed after restart. This is a new API-triggered event in a known API conversation, not arbitrary ChatGPT UI-thread resume.

## Maintenance, rollout and evidence

A persisted leased workspace cursor traverses sixteen shards in bounded one-row pages, retaining progress and wrapping fairly. The worker can submit only proven never-attempted jobs after authorization. It also performs enabled observations/notifications, deadlines and physical replacement cleanup. Unknown initial dispatches and unresolved notifications prevent automatic purge. Cleanup waits for capability expiry even when configured payload retention is shorter, preserving valid duplicate callbacks. Compact deduplication tombstones remain.

Version-1 store-only jobs stay readable; optional jobs use version 2. Global gateway, tracked starts/access, maintenance, polling and parent continuation default off, and normal IaC host-disables the timer. Browser CORS now defaults to an empty allowlist and no credentials. No anonymous MCP bypass or live OAuth enrollment is introduced. Keep the upgraded callback/reader binary when disabling version-2 starts.

See [tracked jobs](tracked-jobs.md), [follow-ups](job-follow-ups.md), [protected acceptance](protected-gateway-acceptance.md) and [implementation progress](implementation-progress.md). Treat source tests, real-adapter tests, native MCP wire checks, deployed health, authenticated ingress and actual agent acceptance as distinct evidence.
