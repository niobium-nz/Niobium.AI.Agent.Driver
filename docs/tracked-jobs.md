# Tracked jobs and results

Stage 2 is an additive extension following merged Stage 1 PR #5 (`a2b701b9e98f500092a27ac2ac8acaef2ce4ca44`). Store-only result exchange is the default tracked mode. Optional provider observation and allowlisted parent continuation are now implemented behind separate disabled flags; see [job follow-ups](job-follow-ups.md). Source deployment is not live OAuth acceptance or permission to invoke agents.

## Tools and contracts

Stage 1 `dispatch_agent` remains persistence-free and waits only for submission acknowledgment. The four additional native MCP tools are `start_agent_job`, `submit_job_result`, `get_job_status` and `get_job_result`. Disabled calls return before authentication or persistence access. Tool discovery does not activate execution.

All four use the validated delegated OAuth identity and scope `AgentGateway.Dispatch`. Version-2 registration/grant rows explicitly distinguish starting from completion. The initial reader policy permits only the authenticated initiating connection. Knowing an opaque job ID is not authority to read or complete it. Use distinct approved connections for initiator and writer; shared identities cannot attest which exact agent runtime used them.

```json
{
  "target_agent": "research-v1",
  "handoff_id": "research-task-001",
  "input": {"task": "Research the supplied items", "data": {"item_ids": ["P100"]}},
  "result_schema": "agent-result.v1",
  "completion_mode": "store_only"
}
```

The optional target `conversation_key` requires global and target-registration reuse permission. `notify_parent` additionally requires the parent feature flag and an operator-provisioned `continuation_ref`; store-only requests reject a continuation reference. No raw parent channel, callback URL, credential or conversation-binding operation is accepted from a model.

The application-owned success shape is exactly `{"summary":"nonblank text","data":{}}`; business failure is exactly `{"code":"safe-lowercase-alias","message":"nonblank safe text"}`. Summary/message limits are 4,096 characters. Canonical result JSON is capped at 16,384 UTF-8 bytes by default; nested data is bounded. Unknown schemas/fields, duplicate properties and unsupported enum values fail closed. Retrieved content is untrusted business data, not authority to change routing or broaden permissions.

B receives a `niobium.agent-job.v1` envelope inside the provider's string `input`, including its job ID, task, result schema, deadline and completion capability. B calls:

```json
{
  "job_id": "<job_v1_ followed by 64 lowercase hexadecimal characters>",
  "completion_capability": "<only the capability delivered to B>",
  "outcome": "succeeded",
  "result": {"summary":"Approved findings", "data":{"findings":[]}}
}
```

Only B's initial trigger envelope contains the capability. It is never returned to A, placed in a parent notification, logged or persisted as a raw token. Completion requires its signature, exact persisted claims, authorized OAuth writer and current registration/grant. Identical valid callbacks return the original receipt; different results conflict.

A failed start response does not prove no job or send exists. Retry the identical target, handoff ID and complete request. Durable identity excludes the payload fingerprint; changed input/schema/conversation/completion settings under an existing identity conflict. Repeating a start after an attempted dispatch does not send again, including after purge.

## Persistence and concurrency

`AgentJob` stores scalar snapshots through Niobium repositories. Its versioned HMAC identity binds workspace, authenticated initiator, immutable target alias and handoff ID. Sixteen deterministic partitions use `workspace:hex-shard`. A separate fingerprint binds the complete canonical request.

Conditional creation precedes external work. A confirmed claim records Sending, owner, generation, first-attempt boundary and lease before HTTP submission. Conditional replacement writes use a unique WriteId and complete intended-snapshot read-back because the pinned adapter may omit response ETags. Never force-save a claim or adopt another writer's ETag as proof of your write.

Dispatch, provider observation, business result and parent continuation are independent axes. A late acknowledgment or observation reloads current state and preserves a racing callback. A stored result suppresses redispatch even if the earlier dispatch is Unknown or Held. Provider Completed does not create a business result; Suspended does not prove human approval. Optional parent delivery starts from a result-plus-Pending transition committed in the same row, not an unreliable second-table insert or post-save event.

Existing version-1 store-only jobs remain readable. Version 2 adds optional scalar run/route/claim fields. Null continuation_ref is omitted from old-style starts, preserving store-only fingerprints. Unknown persisted schema versions/state codes fail closed. The Attempts field counts dispatch claims, not individual immediate HTTP attempts.

Capabilities use the pinned standard JWT library and HS256 only, with a fixed issuer/audience and dedicated versioned keys. Exact canonical token comparison binds job, writer, workspace, target, operation, expiry, nonce and key version. Incoming OAuth and upstream Workspace Agent credentials are not capability keys.

## Maintenance and retention

`MaintainAgentJobs` is a host-disabled minute timer with additional application guards. Defaults are sixteen one-row pages, a forty-five-second tick budget and a ninety-second workspace-cursor lease. The persisted cursor advances after each page and wraps all sixteen shards. Invalid rows and revoked grants count as failures without starving later rows.

The worker can submit only proven never-attempted registrations after reauthorization. Sending-lease expiry becomes Held, not permission for another POST. Unknown/Held B dispatches and parent notifications are not automatically retried across process recovery because the upstream replay window is not verified. A lease fences storage writes, not the external effects of a paused process.

Result deadline defaults to twenty-four hours plus a one-hour capability grace period. A valid late callback can change DeadlineExceeded to Available with a late flag; the deadline does not cancel B. Known Accepted/Rejected jobs with no result become ClosedWithoutResult after capability expiry.

Cleanup requires known initial disposition, elapsed payload retention AND expired callback capability. This preserves duplicate-result receipts during long callback windows even when retention is short. Pending/Sending/Unknown/Held parent notifications prevent automatic purge. Cleanup uses physical replacement to remove payload properties, retaining compact deduplication fingerprints and permitted status/hash metadata. Retrieval reports Purged, never an empty substitute result. Tombstones are not automatically deleted.

This is modest-workload partition scanning, not an index over arbitrary due-time properties. Metrics contain only bounded operational counters. No global quota, high-scale scheduler, exactly-once external execution or automatic resolution of Held jobs is claimed.

## Configuration and rollback

All execution defaults are off. Normal `infra/main.bicepparam` provisions only AgentRegistration/AgentGrant. Explicit `infra/tracked-jobs.bicepparam` stages AgentJob/AgentMaintenance. Explicit `infra/parent-continuation.bicepparam` additionally stages AgentContinuationRoute. Neither staging file activates execution. Use only approved accounts.

| Setting | Purpose |
| --- | --- |
| `AgentGatewayOptions__EnableTrackedJobs` | New starts and proven never-attempted recovery; default false |
| `AgentGatewayOptions__EnableTrackedJobAccess` | Completion and initiating-principal reads; required by tracked features |
| `AgentGatewayOptions__EnableJobMaintenance` | Bounded maintenance; default false |
| `AgentGatewayOptions__EnableProviderRunPolling` | Opt-in beta run metadata for new jobs and read-only observations; default false |
| `AgentGatewayOptions__EnableParentContinuation` | Allow configured notify_parent starts and pending parent notifications; default false |
| `AzureWebJobs.MaintainAgentJobs.Disabled` | Host switch; normal IaC sets true |
| `TrackedJobOptions__IdentityKey` | Dedicated random 32-64 byte canonical-base64 key via Key Vault; stable while tombstones exist |
| `TrackedJobOptions__ActiveSigningKeyVersion` | Allowlisted version for newly issued capabilities |
| `TrackedJobOptions__SigningKeys__<version>` | Independent random 32-64 byte HS256 key via Key Vault; retain through outstanding expiry |
| `TrackedJobOptions__ResultDeadlineMinutes` | Default 1,440; maximum 10,080 |
| `TrackedJobOptions__CallbackGraceMinutes` | Default and maximum 60 |
| `TrackedJobOptions__PayloadRetentionDays` | Default 30; range 1-365 |
| `TrackedJobOptions__MaintenanceMaxRowsPerTick` | Default 16 one-row pages; maximum 64 |

Upgrade configuration conditionally to version 2 with an immutable result schema/writer and explicit CanStartJob/CanSubmitResult permissions. Version-1 grants never inherit job permissions; CanDispatch remains separate. Additional result-reader policies are not implemented.

Stop new work with EnableTrackedJobs=false while retaining access, keys and safe maintenance for existing jobs. Pause polling/notifications independently. A global gateway/access disable also blocks callbacks and retrieval. Keep the upgraded binary: pre-version-2 binaries cannot safely read version-2 jobs. Retain old signing keys until issued capabilities expire. Identity-key rotation requires a separate compatibility migration; in-place rotation defeats deduplication.

Ordinary IaC restores all gateway/job/follow-up flags off. Real activation requires a reviewed authenticated-health/pipeline transition, actual OAuth evidence, approved Table/RBAC/Key Vault inputs and safe telemetry verification. [Protected gateway acceptance](protected-gateway-acceptance.md) supplies read-only checks, not a fabricated live pass. Harmless store-only and parent API-conversation trials still need approved agents, inputs and execution authorization.
