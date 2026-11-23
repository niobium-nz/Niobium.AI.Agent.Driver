# Store-only tracked jobs

This is the separately requested Stage 2 implementation following merged Stage 1 PR #5 (`a2b701b9e98f500092a27ac2ac8acaef2ce4ca44`). It implements the store-only core of work packages S2.1, S2.2 and S2.3. It does not establish live ChatGPT OAuth acceptance, enable external dispatch, implement beta provider polling, or automatically continue a parent agent.

## Tool boundary

Stage 1 `dispatch_agent` remains unchanged: it never writes a job, waits only for submission acknowledgment, and has no durable recovery. Use it when no stored result is required.

The native MCP endpoint also advertises four additional tools. They return `tracked_jobs_disabled` without authentication or persistence access until the relevant flags are enabled. Merely discovering a tool does not activate it.

| Tool | Behavior |
| --- | --- |
| `start_agent_job` | Persist an immutable request before attempting submission; return the opaque job identity and actual dispatch/result states. |
| `submit_job_result` | Commit the first valid result under both a current completion grant and a job-scoped signed capability. Identical retries return the original receipt; different results conflict. |
| `get_job_status` | Read the authenticated initiator's persisted states. No provider status is inferred. |
| `get_job_result` | Return the initiator's immutable result or an explicit pending/deadline/closed/purged state. |

All four use the same validated delegated OAuth identity as Stage 1. The delegated scope is still `AgentGateway.Dispatch`; v2 registry grants distinguish starting from completion. The completion principal should be a different approved connection from the initiator. A capability is not proof that a particular agent runtime ran when several agents share one OAuth identity.

## Start and result contracts

```json
{
  "target_agent": "research-v1",
  "handoff_id": "research-task-001",
  "input": {"task": "Research the supplied items", "data": {"item_ids": ["P100"]}},
  "result_schema": "agent-result.v1",
  "completion_mode": "store_only"
}
```

`conversation_key` is optional and requires both existing global and registration reuse permissions. `notify_parent`, `continuation_ref`, arbitrary schemas and unknown fields are rejected, not silently ignored. The application-owned `agent-result.v1` success shape is exactly `{"summary":"nonblank text","data":{}}`; its business-failure shape is exactly `{"code":"safe-lowercase-alias","message":"nonblank safe text"}`. Summary/message limits are 4,096 characters. The complete canonical result is limited to 16,384 UTF-8 bytes by default. The nested data object may contain bounded JSON, but no result content grants authority or changes routing.

B receives a `niobium.agent-job.v1` envelope inside the provider's string `input`, containing the job ID, original input, result schema, deadline and completion capability. Its callback is:

```json
{
  "job_id": "<job_v1_ followed by 64 lowercase hexadecimal characters>",
  "completion_capability": "<only the capability delivered to B>",
  "outcome": "succeeded",
  "result": {"summary":"Approved findings", "data":{"findings":[]}}
}
```

Never log or copy the capability into a report. Only the initial task envelope sent to B contains it. Starts and read tools never return it. General-purpose Table entities contain its immutable nonce/key version/claims inputs, not the raw signed token. Both capability and authenticated principal must match the persisted job; the current v2 grant and target binding are checked again on submission.

A failed start response is not proof that no job or external submission exists. Retry the identical request with the same target and handoff ID. A repeated start returns the same durable identity and does not send again after an attempted dispatch. Input, schema, conversation and completion-mode changes under that identity conflict, including after payload purge.

## Persistence and concurrency

`AgentJob` stores scalar snapshots through Niobium repositories. The job ID is a versioned HMAC-SHA256 digest of workspace, validated initiating principal, target alias and handoff ID, using a dedicated identity key. The payload fingerprint does not determine the identity. Sixteen partitions per workspace use `workspace:hex-shard`.

Conditional create precedes all outbound work. A confirmed conditional claim records `Sending`, owner, generation, first-attempt time and lease before calling the provider. Every conditional write uses a new WriteId and a complete intended-snapshot read-back check because the pinned adapter can omit write-result ETags. Writes use repository replacement semantics, never force-save or merge-null cleanup.

Dispatch and result states are independent. Acceptance saving reloads current state and preserves a result committed by a racing callback. `Available` suppresses all redispatch attempts even when dispatch remains Unknown or Held. `ProviderState` is always `unobserved` and continuation is always `not_requested` in this release; neither is a claim about the actual target's run.

HS256 capability headers and immutable claims are constructed deterministically with the pinned Microsoft JWT library. Verification allows only HS256 and binds the exact audience, operation, workspace, target, writer, job, expiry, nonce and signing-key version. Validation uses both the signature and canonical equality with the persisted claims. The incoming OAuth bearer and the upstream Workspace Agent token are never reused as capability keys.

## Recovery, deadlines and retention

`MaintainAgentJobs` is an optional minute timer, host-disabled in normal deployments and additionally guarded by application flags. It scans at most 16 one-row pages per tick by default, under a 45-second budget and 90-second workspace cursor lease. The checkpoint retains shard and continuation after each page, eventually wraps through all sixteen shards, and does not let a permanently invalid row or revoked grant prevent progress.

The worker may dispatch a job proven never attempted, after rechecking current grants and immutable target bindings. An expired Sending lease becomes Held. Unknown and Held submissions are never automatically retried because the upstream replay window has not been verified. A lease fences conditional writes; it cannot prove that a paused process never reached the external provider. Investigate ambiguous jobs rather than resetting them to Registered.

The result deadline defaults to 24 hours, with a one-hour capability grace period. A valid result within the grace period can turn DeadlineExceeded into Available, with a late-arrival flag. DeadlineExceeded never means the remote agent was canceled. Known Accepted/Rejected jobs without results become ClosedWithoutResult after the grace period. Result/input payloads are removed after the configured retention period, default 30 days; Unknown/Held jobs are conservatively retained. Compact identity/fingerprint tombstones are not automatically deleted, so repeating an old handoff does not create a new submission. Retrieval after purge returns `purged`, not an empty object pretending to be the old result.

Partition scanning is intended for modest workloads. `NextActionUtc` is not assumed to be indexed. Safe timer metrics report pages, rows, actions, failures and duration only. This release does not claim a global quota, arbitrary-scale scheduler, exactly-once external execution, or automatic resolution of Held jobs.

## Configuration, rollout and rollback

Defaults are off. The normal `infra/main.bicepparam` still provisions only the two Stage 1 configuration tables. `infra/tracked-jobs.bicepparam` is an explicit storage-staging alternative adding `AgentJob` and `AgentMaintenance`; it does not turn on the application or worker. Do not run it against an unapproved account.

| Setting | Purpose |
| --- | --- |
| `AgentGatewayOptions__EnableTrackedJobs` | Permit new tracked starts and never-attempted recovery. Default false. |
| `AgentGatewayOptions__EnableTrackedJobAccess` | Permit completion and initiator retrieval. Default false. Required by new starts and maintenance. |
| `AgentGatewayOptions__EnableJobMaintenance` | Permit bounded cursor/deadline/retention maintenance. Default false. |
| `AzureWebJobs.MaintainAgentJobs.Disabled` | Host switch; ordinary infrastructure sets true. |
| `TrackedJobOptions__IdentityKey` | Dedicated random 32-64 byte key, canonical base64, projected through Key Vault. Keep stable while any deduplication tombstones exist. |
| `TrackedJobOptions__ActiveSigningKeyVersion` | Allowlisted logical signing-key version for new jobs. |
| `TrackedJobOptions__SigningKeys__<version>` | Independent random 32-64 byte HS256 key in Key Vault-backed configuration. Retain old versions until all issued capabilities expire. |
| `TrackedJobOptions__ResultDeadlineMinutes` | Default 1,440, maximum 10,080. |
| `TrackedJobOptions__CallbackGraceMinutes` | Default and maximum 60. |
| `TrackedJobOptions__PayloadRetentionDays` | Default 30, range 1-365. |
| `TrackedJobOptions__MaintenanceMaxRowsPerTick` | One-row page budget, default 16, maximum 64. |

Upgrade target registrations conditionally from version 1 to version 2, specifying `result_schema: "agent-result.v1"` and `result_writer_principal_key`. Target channel/credential bindings cannot be changed in place; v2 result schema and writer are also immutable. The operator command already supports these scalar additions. Upgrade grants to version 2 with explicit `can_start_job` or `can_submit_result`; version 1 grants never gain those permissions implicitly. Stage 1's `can_dispatch` continues independently. Only initiator reads are implemented; additional result-reader grants are not yet supported.

Stage 2 needs a separately reviewed activation change that stages tables, projects real secrets through the existing Key Vault path, verifies real OAuth and telemetry privacy, and then enables the intended flags. Ordinary infrastructure currently restores the gateway and job flags to false. Do not activate by hand and assume a future deployment preserves it.

For a normal rollback, set only `EnableTrackedJobs=false` to stop new starts and never-sent recovery, while retaining `EnableTrackedJobAccess=true`, required keys and safe maintenance for jobs already issued. Disabling the global gateway or access flag is an emergency full stop and also blocks callbacks/retrieval. Do not remove callback tools or old signing keys while B jobs are in flight. Rotating the identity key requires an explicit migration/compatibility lookup; changing it in place would defeat deduplication and is not supported.

## Remaining original-plan work

Real ChatGPT OAuth discovery/redirect/refresh, all ingress negative tests, restrictive CORS, authenticated health probes, platform telemetry redaction and an authorized A-to-B/B-to-gateway trial remain live acceptance work. No token or approved API-channel input was fabricated for this implementation.

Optional provider polling and parent continuation (S2.3 optional adapter and S2.4) remain unimplemented and unavailable. There is no AgentContinuationRoute table, model-facing route creation, arbitrary UI-thread resume, completion notification outbox or provider status inference. S2.5 production rollout and operational acceptance remain gated on real evidence and independent review. The repository progress/evidence files and PR identify tests actually observed, distinct from source authored here.
