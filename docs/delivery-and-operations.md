# Delivery and operations

The existing delivery and independent-review policy remains in force. Do not merge or mark readiness from source inspection. The normal feature workflow restores/builds, compiles Bicep, runs policy/unit/Azurite/native-MCP tests, enforces unchanged coverage gates, and only then proceeds to the repository's existing test deployment and independent health verification. Production remains merge-gated. This work does not merge, publish a package or authorize live Workspace Agent execution.

Contracts and Core use project references and `IsPackable=false`. The pre-existing publish workflow also requires an explicit `PUBLISH_CONTRACTS=true` decision; enabling package publication requires a separate reviewed packaging change, not merely setting that variable.

## Activation and rollback

Ordinary IaC deployments deliberately restore `AgentGatewayOptions__Enabled=false`. Activation requires a reviewed change after the OAuth/client, upstream and telemetry acceptance evidence is recorded. Do not patch enabled=true by hand and assume it survives a later deployment. `gateway-auth.bicep` protects all paths; update the existing health harness to carry appropriate authentication when adopting it. The scaffold CORS policy and OAuth discovery/registration must be reviewed against the actual connected client before activation.

To stop new dispatches, disable the gateway, disable a registration or revoke the principal grant through the conditional operator command. In-flight requests may already have been accepted; disabling cannot undo them. Keep old aliases and credential bindings stable during an uncertain retry window. A new channel/account binding gets a new alias and explicit cutover, not an in-place edit.

Rotate a token only within the same approved account/channel scope and trusted credential alias. Review Key Vault reference resolution without printing the value. Unknown or unresolved aliases fail closed. Do not delete registry rows as a substitute for investigated rollback.

## Incident outcomes

| Outcome | Operator interpretation |
| --- | --- |
| `accepted` | Provider accepted submission; independently inspect the target's approved destination/run UI for completion. |
| `not_dispatched` | This invocation did not begin an upstream send; resolve the safe error category. |
| `rejected` | A definitive rejection was observed with no earlier uncertain attempt. Configuration failures need operator action. |
| `outcome_unknown` | A send may have been accepted. Do not change the ID or payload for a retry and do not claim non-execution. |

Correlate using the opaque event digest, target alias, outcome, attempts and duration. Do not add raw task, token, bearer, provider body or channel logging during diagnosis. There is no gateway status endpoint, result store, durable submission retry or recovery job in Stage 1. Escalate ambiguous results instead of introducing ad hoc polling/queues.
