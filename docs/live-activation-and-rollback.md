# Live activation and rollback

Use the approved TEST environment and recording procedure in the [live acceptance runbook](live-acceptance-runbook.md). Apply approved durable environment-specific settings through the existing deployment configuration. The ordinary deployment restores the execution defaults, so coordinate each feature push with the operator for any active trial. Production enablement requires a separate decision.

## Activation settings

Record each effective value from the deployed app and confirm storage, authentication, credential reference resolution and registry state before changing the next setting. Azure setting names have the `AgentGatewayOptions__` prefix unless specified otherwise below.

| Stage | Global `Enabled` | Tracked starts / access | Maintenance / host-disabled | Polling / parent | Other conditions |
| --- | --- | --- | --- | --- | --- |
| Protected baseline | false | false / false | false / true | false / false | Mandatory ingress and authorized health tested separately |
| Registry-only | true | false / false | false / true | false / false | No executable registration and grant combination |
| Store-only | true | true / true | false / true | false / false | Approved synthetic start and completion only |
| Observation | true | Approved trial / true | true / false | true / false | Inventory and configured bounds verified |
| Parent | true | Approved trial / true | true / false | Explicit decision / true | Exact expiring route, parent grant and reuse settings verified |
| Quiescent | As required for issued access | false / retain as needed | false / true | false / false | Stage 1 dispatch separately quiesced |

Tracked starts use `EnableTrackedJobs` and completion/result reads use `EnableTrackedJobAccess`. The worker requires `EnableJobMaintenance=true` plus `AzureWebJobs.MaintainAgentJobs.Disabled=false`. Its follow-up settings are `EnableProviderRunPolling` and `EnableParentContinuation`. Parent delivery also requires `AllowConversationReuse` on the gateway and matching parent registration, current authorization and route binding. An enabled timer can act on older eligible work, so verify its inventory and budget first.

## Graceful rollback

1. Confirm TEST scope, preserve the reviewed compatible reader/callback build, and record the deployed SHA, effective flags, counts and owners. Capture a safe reference to each pending, sending, unknown or held initial dispatch and notification. Record capability expiries and required signing versions privately.
2. Set `EnableTrackedJobs=false`, `EnableProviderRunPolling=false`, and `EnableParentContinuation=false` through the approved configuration path. Initially set `EnableJobMaintenance=false` and `AzureWebJobs.MaintainAgentJobs.Disabled=true`. Confirm all effective values and account for any execution already in flight.
3. Keep `Enabled=true` and `EnableTrackedJobAccess=true` for authorized callbacks and reads of issued jobs that still need access. Preserve the current writer grant and compatible result bindings for those jobs. Quiesce Stage 1 by a scoped conditional update to its dispatch permissions or registration, checking the current ETag and the intended WriteId snapshot. Preserve registrations and completion permissions required by issued callbacks.
4. Verify a new tracked start is denied, the timer is stopped, and no additional provider GET or parent POST occurs in the observation window. Use only the approved existing fixture to confirm authorized callback and retrieval still work. Preserve the result and tombstone after identical retries. Never repeat an uncertain external send with a new ID or reset its row state. Record who reconciles each unresolved outcome and whether deadline and retention work can resume.
5. Retain the stable identity key and relevant signing versions. Leave pending or uncertain records and deduplication tombstones in place. Remove unused grants or routes only through approved conditional updates once work depending on them is resolved. Verify exact-commit health and retained authorization after configuration settles.

Global disable is an emergency stop. It also stops completion and result access. Record the access impact, restore callback/read availability when approved, and keep the version-2-aware build for existing version-2 rows. The prior configuration snapshot and reviewed run identify the inverse changes. If an effect may already be external, suspend the affected path and assign reconciliation to the operator.

## Verification

Use the isolated `AgentJobMaintenanceTests`, `AgentJobSecurityTests`, `AgentFollowUpTests` and real Table adapter tests for claims, retained state, result and callback access, retention and route revocation. Record this separately from actual TEST setting restoration, worker state and outstanding work in the [evidence record](live-acceptance-evidence.md).
