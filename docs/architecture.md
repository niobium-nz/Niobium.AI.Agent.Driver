# Agent Gateway architecture

The [original handover](niobium-agent-gateway-handover/Niobium.AgentGateway.Implementation-Plan.md) separates dispatch-only Stage 1 from optional durable Stage 2. Stage 1 was merged in PR #5. PR #6 adds the disabled [store-only tracked-job extension](tracked-jobs.md). Neither source deployment proves live OAuth compatibility or authorizes external agent execution.

## Boundaries

`Server -> Core -> Contracts`. Contracts contain request/result records and finite wire enums. Core owns configuration/job entities, rich Niobium domains, deterministic serialization, identity derivation, validation, state transitions and coordinating flows. Server owns native Azure Functions MCP, delegated token validation, capability signing, outbound HTTP, timer entry point and composition. Azure SDK clients occur only in framework adapters and integration-test fixtures, not application flows.

At `/runtime/webhooks/mcp`, `list_agents` and `dispatch_agent` retain their Stage 1 contracts. `start_agent_job`, `submit_job_result`, `get_job_status` and `get_job_result` are additive and disabled by default. Native trigger functions invoke tool-boundary services; responses contain structured objects with matching JSON text. Dispatch/start/submit are consequential, not read-only. The optional maintenance function is a host-disabled minute timer with independent application guards.

The MCP extension's typed argument converter can coerce numbers, date/GUID strings and case-distinct keys. Dispatch and job tools use original binding JSON for model data and the typed transport only for validated identity. No ordinary HTTP route impersonates MCP. The real local host probe checks all six advertised tools with execution disabled.

## Configuration and authorization

`AgentRegistrationDomain` checks enabled state, version, workspace, target alias and trusted credential mapping. `AgentGrantDomain` checks current principal partition, permission and expiry. Security decisions read current repository rows rather than cached initialized domain snapshots. Niobium forced lookups alone are insufficient to invalidate every partition-domain cache.

Version-1 rows remain dispatch-only. Version-2 registrations add an application-owned result schema and immutable completion-writer principal. Version-2 grants explicitly separate CanDispatch, CanStartJob and CanSubmitResult. No job permission is inferred from an old dispatch grant. The initial result-reader policy permits only the validated initiating principal in the same workspace.

`McpCallerContextAdapter` independently validates the transport bearer against the configured single-tenant issuer, resource audience, delegated scope and approved clients. Result submission additionally validates a standard signed job capability and current completion grant. Incoming OAuth credentials, managed identity, upstream Workspace Agent token and capability keys have separate purposes.

The Server explicitly composes `Niobium.Platform.StorageTable`. Ordinary infrastructure parameters provision only AgentRegistration/AgentGrant. An explicitly selected tracked-job parameter file stages AgentJob/AgentMaintenance. Operator registry writes use exact ETags and full WriteId read-back verification; no agent-facing management endpoint is exposed.

## Stage 1 submission

The Stage 1 writer rejects unknown fields, duplicate JSON keys, invalid identifiers, nonobject data and excessive canonical UTF-8 input, depth or property counts. It sorts object keys, preserves array order/text/numeric tokens and checks the final escaped provider body size. Target and handoff identity remain stable across retries, with no gateway execution ledger.

Only `https://api.chatgpt.com` and the fixed validated channel route are used. Redirects and additional retry/logging handlers are removed from the typed client pipeline. Response headers determine acceptance: a known 202 does not depend on optional body parsing. Immediate retries use frozen bytes and one idempotency key, at most three attempts, eight seconds per attempt and a linked 25-second tool-call budget. A subsequent rejection cannot erase an earlier uncertain outcome.

Stage 1 never writes jobs, results, queues or recovery state. Acceptance is not completion; it cannot enforce permanent deduplication or detect later changed-input reuse of an event ID.

## Store-only durable jobs

`AgentJob` owns versioned key derivation, scalar physical schema, persisted finite-state codes and safe projections. Its keyed identity depends on workspace, authenticated initiator, target alias and logical handoff ID; a separate immutable fingerprint binds the complete canonical start contract. A create is committed and reconciled before any HTTP send.

`AgentJobDomain` owns conditional registration, fenced dispatch claims, acknowledgment recording, immutable first-result acceptance and retention. Every mutation uses a unique WriteId and read-back comparison of the complete intended snapshot, excluding only ETag. Adapter write responses alone are not proof of ownership. Fresh ETags from an unrelated writer are never adopted to complete an old intended write.

Dispatch and result axes are independent. Recording an acknowledgment reloads current state and preserves a callback that already stored a result. Duplicate callbacks require identical outcome, canonical JSON and result hash. Get operations do not include task input, routing, credentials, leases or capabilities. Provider state stays unobserved and continuation stays not_requested until separate implementations exist.

`AgentJobFlow` uses domain repositories and the existing bounded provider client. The serialized provider input is a distinct niobium.agent-job.v1 envelope containing B's signed capability. The raw capability is deterministically reconstructed from frozen claims and the stored signing-key version; it is not persisted or returned to A. The durable Attempts field counts submission claims, not individual immediate HTTP attempts inside the provider client.

`AgentMaintenanceDomain` owns a leased workspace cursor through sixteen partitions. `AgentJobMaintenanceFlow` processes bounded one-row pages, persists progress after each page and eventually wraps. It may submit only proven never-attempted jobs after reauthorization. Expired Sending leases become Held; ambiguous sends are not automatically retried. Deadline updates and physical replacement cleanup preserve immutable deduplication tombstones. This is a modest-scale scanning design, not an assumed index over arbitrary Table properties.

## Activation boundary

Global gateway, tracked starts/access and maintenance default off. The timer is also host-disabled by ordinary IaC. No anonymous native MCP bypass, real OAuth enrollment, live agent execution or automatic parent continuation is included. Maintain separate evidence for source tests, actual-adapter persistence, native wire compatibility, deployed health, authenticated ingress and upstream agent acceptance. See [implementation progress](implementation-progress.md) for completed and remaining work.
