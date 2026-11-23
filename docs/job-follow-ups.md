# Optional provider observation and parent continuation

This extends the disabled tracked-job implementation in PR #6. Both features are optional and remain off in application defaults and ordinary infrastructure deployments. The existing Stage 1 dispatch tool remains persistence-free and does not request run metadata. No live agent has been invoked as part of authoring these changes.

## Provider observation

The official [Workspace Agents trigger/run reference](https://developers.openai.com/workspace-agents/trigger-runs) was checked on November 23, 2026. The optional POST header is `OpenAI-Beta: workspace_agent_runs=v1`. The accepted response may include `agent_trigger_run_id` with an `apirun_` identifier. The observation request is `GET /v1/workspace_agents/{api_channel_id}/runs/{agent_trigger_run_id}` at the fixed `https://api.chatgpt.com` origin. The documented GET does not require the beta header. Validate `object=workspace_agent.trigger_run`, matching `id` and `api_trigger_id`, and map queued, in_progress, suspended, completed and failed. Unknown states remain `unrecognized`, never permission to retry a submission.

`AgentGatewayOptions__EnableProviderRunPolling=true` opts new tracked jobs into metadata capture. This is server configuration, not a model-supplied API path. Capture is bounded to one second and at most the configured provider-body limit. A known HTTP 202 remains accepted if metadata is absent, malformed, too large or times out. A missing ID means no polling; it never causes another POST. Ordinary Stage 1 and parent notification POSTs do not request metadata.

Maintenance claims one bounded observation at a time. The poll lease is sixty seconds, longer than the maximum eight-second default HTTP attempt. The next attempt is no sooner than sixty seconds, with Retry-After bounded between sixty seconds and one hour. The overall existing maintenance row/time budget still applies. Terminal completed/failed observations stop polling. Observation also stops at the capability reconciliation deadline or payload purge. Unknown states remain observable within that window. Current owner grant and target binding must still permit the operation. Provider details and response bodies are not logged or returned.

Provider status and business result are independent. Completed does not manufacture a result; suspended does not prove a human approval request. A callback may commit while a poll is running. Conditional read/merge preserves the callback even if the observed run later fails. `get_job_status` returns only the finite provider state, not raw upstream IDs or private errors.

## Parent continuation

Enable `AgentGatewayOptions__EnableParentContinuation` only after an explicitly authorized API-conversation trial. `start_agent_job` then accepts `completion_mode: notify_parent` and a required `continuation_ref`. Store-only calls must not include a continuation reference. There is no generic resume tool, callback URL or raw parent conversation argument.

An operator configures an `AgentContinuationRoute` using the existing local registry command, not an MCP tool. The row binds workspace, reference, initiating principal, parent alias, API channel, credential alias, the exact upstream API conversation key, enabled flag and expiry. Owner and destination are immutable; change of destination requires a new reference. Updates require the current ETag, and complete WriteId read-back confirms the result. The command's receipt contains no conversation key or credentials.

Example shape, with placeholders to replace outside source control:

```json
{
  "expected_e_tag": null,
  "continuation": {
    "workspace_key": "approved-workspace",
    "continuation_ref": "parent-route-v1",
    "initiator_principal_key": "REPLACE_WITH_VALIDATED_PRINCIPAL_DIGEST",
    "parent_target_agent": "parent-v1",
    "api_channel_id": "agtch_REPLACE",
    "credential_alias": "approved-token",
    "provider_conversation_key": "REPLACE_WITH_ACTUAL_API_CONVERSATION_KEY",
    "expires_utc": "REPLACE_WITH_APPROVED_FUTURE_UTC",
    "enabled": true
  }
}
```

Use the actual serialized property names emitted by GatewayJson/options; the ETag property is `expected_e_tag` under the existing snake-case serializer. Do not substitute a chat URL for the API conversation key. The first external invocation of A must use this exact operator-configured key. The gateway checks the initiating principal, not which UI chat issued the MCP call. A shared OAuth principal does not cryptographically distinguish two agents.

The parent registration must be enabled with conversation reuse allowed, its channel and credential binding must match the route, and the initiating principal needs a current CanDispatch grant for the parent alias. Global AllowConversationReuse is also required. These checks are repeated before notification; disabling/deleting/rebinding the route or revoking the grant stops unsent delivery. Both snapshot expiry and the current route expiry apply. If a route becomes unauthorized, pending notification is Held for operator review rather than silently redirected.

On first valid callback, the result and `Continuation=Pending` are committed in the SAME conditional AgentJob write. No cross-table atomicity is assumed. The callback returns after persistence; it does not dispatch A inline. The existing timer later claims the pending outbox, reconstructs fixed event bytes and submits a `niobium.agent-result-ready.v1` envelope containing only event_id, job_id and result_hash. A retrieves the result with its authorized connection. Both the event key and provider idempotency key are stable and separate from B's submission. Result payloads and capabilities are not copied into the notification.

Duplicate identical callbacks never reset the outbox. Sending-lease expiry becomes Held. Unknown notification outcomes remain Unknown, never an automatic new POST after restart. A confirmed accepted notification means submission, not completion or confirmed reading by A. Pending/Sending/Unknown/Held notification payloads are retained for reconciliation, not automatically purged. No arbitrary original ChatGPT UI conversation can be resumed by this feature.

## Persistence and rollback

Job schema version 1 remains readable for store-only work. Jobs opting into either follow-up use version 2 with additive scalar fields. Read and mutation paths reject unknown versions/state codes. Existing store-only request fingerprints are preserved because the new nullable continuation_ref is omitted when absent.

To pause new jobs, turn off EnableTrackedJobs while retaining EnableTrackedJobAccess for issued callbacks/results. Pause notifications/polling with their independent switches. Leave the upgraded callback/reader code deployed: a binary rollback to a pre-version-2 implementation cannot safely process version-2 jobs. Ordinary IaC deliberately restores all execution flags off; activation requires its own reviewed deployment change, not a temporary manual flag patch.

Stage storage separately through `infra/parent-continuation.bicepparam` only after approval. It includes the two configuration tables, AgentJob, AgentMaintenance and AgentContinuationRoute. It does not enable any feature. No queue, blob result store or orchestration engine is added. Retention now waits until capability expiry before purging results, preserving valid duplicate callbacks even with short payload retention.

## Verification and remaining external acceptance

The added unit and real-adapter tests cover atomic outbox updates, callback/acknowledgment/poll races, competing/fenced claims, malformed or oversized metadata, missing run IDs, current-route and grant revocation, immutable route writes, stable parent bytes, unknown outcomes, rollback flags and payload removal. Record exact-head CI results in the PR; source or mocks are not a live acceptance pass.

Still required before activation: actual ChatGPT/Entra OAuth discovery, actual client and redirect configuration, refresh, trusted principal propagation and scope/audience checks; negative tests on every ingress path; authenticated health probes; restrictive CORS; platform telemetry/body redaction; approved Table/RBAC/Key Vault inputs; a harmless authorized A-to-B result trial; and a separate trial proving the configured parent API conversation receives the notification. These require actual tenant/workspace setup and approved test identities, not fabricated IDs, source changes alone, or an implementation agent's approval. No automatic acceptance, production activation or package publication is implied.
