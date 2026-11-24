# Delivery and operations

The existing delivery and independent-review policy remains in force. Do not merge or mark readiness from source inspection. The normal feature workflow restores/builds, compiles Bicep, runs policy/unit/Azurite/native-MCP tests, enforces unchanged coverage gates, and only then proceeds to the repository's existing test deployment and independent health verification. Production remains merge-gated. This work does not merge, publish a package or authorize live Workspace Agent execution.

Contracts and Core use project references and `IsPackable=false`. The pre-existing publish workflow also requires an explicit `PUBLISH_CONTRACTS=true` decision; enabling package publication requires a separate reviewed packaging change, not merely setting that variable.

## Activation and rollback

Ordinary IaC deployments restore execution flags to their disabled states. Make any approved TEST enablement durable through reviewed environment configuration. `gateway-auth.bicep` protects all paths. Coordinate the authenticated health workflow and actual OAuth enrollment before applying it. [Live acceptance runbook](live-acceptance-runbook.md)

Use the [graceful rollback procedure](live-activation-and-rollback.md#graceful-rollback) to pause tracked starts and optional follow-ups while retaining authorized access for issued work. Quiesce Stage 1 dispatch separately through a scoped conditional grant or registration update. An in-flight request may already be accepted. Preserve existing aliases and credential bindings through reconciliation. A channel or account change requires a new alias and an approved cutover.

Rotate a token only within the same approved account/channel scope and trusted credential alias. Review Key Vault reference resolution without printing the value. Unknown or unresolved aliases fail closed. Do not delete registry rows as a substitute for investigated rollback.

## Incident outcomes

| Outcome | Operator interpretation |
| --- | --- |
| `accepted` | Provider accepted submission; independently inspect the target's approved destination/run UI for completion. |
| `not_dispatched` | This invocation did not begin an upstream send; resolve the safe error category. |
| `rejected` | A definitive rejection was observed with no earlier uncertain attempt. Configuration failures need operator action. |
| `outcome_unknown` | A send may have been accepted. Do not change the ID or payload for a retry and do not claim non-execution. |

Correlate using the opaque event digest, target alias, outcome, attempts and duration. Do not add raw task, token, bearer, provider body or channel logging during diagnosis. There is no gateway status endpoint, result store, durable submission retry or recovery job in Stage 1. Escalate ambiguous results instead of introducing ad hoc polling/queues.
