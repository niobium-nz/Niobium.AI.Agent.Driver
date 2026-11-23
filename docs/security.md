# Agent Gateway security boundary

## Identities and trust

There are three independent identities: the incoming ChatGPT-connected Entra principal/client, the Function App managed identity for Azure resources, and the owner/scope of the server-held Workspace Agent token. No incoming bearer is forwarded upstream. Tokens never belong in tool arguments, results, registration rows, task envelopes or diagnostic exception text.

The gateway does not authenticate an exact Workspace Agent name. Two agents using the same connection/principal may be indistinguishable and share its grant boundary. Different privileges require independently distinguishable authenticated connections, verified in the actual ChatGPT builder.

`McpCallerContextAdapter` verifies the bearer cryptographically with trusted single-tenant Entra discovery/JWKS and exact issuer/audience/lifetime checks. It ignores `X-MS-CLIENT-PRINCIPAL` and other claimed identity headers as identity evidence. Caller-supplied workspace/source agent/callback URL/token/channel fields are not accepted. Only Streamable HTTP transport is accepted.

## Deployment ordering is a release gate

This change never sets the MCP webhook authorization level to Anonymous. The app and IaC both remain disabled. `infra/gateway-auth.bicep` is a separately reviewed template for mandatory App Service authentication, HTTPS and unauthenticated 401 responses. It does not enable the gateway, configure a guessed ChatGPT redirect, or change the webhook key level.

Before activation, prove mandatory OAuth on every reachable execution path, including the raw Azure hostname. Test without a bearer, with spoofed platform headers, with invalid/expired/wrong-audience tokens, without scope and with an unauthorized client/principal. Test with a valid MCP system key during the pre-bypass stage so a 401 from key authentication is not mistaken for proof of OAuth. Configure discovery/registration using the current actual ChatGPT OAuth flow; source inspection is not proof of compatibility.

Only after that evidence may a reviewed deployment set `AzureFunctionsJobHost__extensions__mcp__system__webhookAuthorizationLevel=Anonymous` behind mandatory OAuth. Do not combine unverified authentication setup and anonymous activation in one deployment. The final activation change must also update authenticated health/deployment probes and eliminate the scaffold's wildcard CORS configuration as appropriate to the verified client. The current public health probe is scaffold readiness metadata, not evidence that MCP authentication works.

## Authorization and outbound isolation

Only enabled, version-1 registrations with allowlisted credential aliases and current principal grants may dispatch. Expiry, enabled state and permission are evaluated on the current request. Current rows are explicitly reloaded even after cached domain listings. Registration channel and credential alias bindings cannot be rebound in place.

The provider origin and route shape are fixed, channel characters are constrained, redirects are disabled and no tool accepts arbitrary URLs or headers. The typed HTTP pipeline has a single retry owner. Server logging records only an opaque event digest, public target alias, outcome, attempt count and duration; it does not read provider response bodies or log exception objects. Review platform/Azure SDK telemetry configuration as part of deployment acceptance to ensure request/body/header capture is disabled; application tests alone do not certify platform telemetry.

`dispatch_agent` is consequential and may incur charges or activate a more privileged agent. It is not marked read-only. Approval to dispatch does not authorize every action the target could perform. Keep target-agent destination restrictions and approval requirements intact.

## Limits and known boundaries

Stage 1 keeps no execution ledger. It cannot enforce permanent exactly-once delivery, detect a later changed-input reuse of an ID, reconcile unknown outcomes or recover work after a timeout. An `outcome_unknown` response is not failure proof and must not cause a fresh ID retry. No background continuation, polling, callback, queue or status row has been added.
