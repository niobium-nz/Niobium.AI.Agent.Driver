# Compatibility evidence and activation gates

Evidence distinguishes source inspection, authored tests and tests actually executed. The implementation does not claim a live ChatGPT connection or a successful Workspace Agent trigger.

## Reviewed baseline and decisions

- Driver baseline: `56c92c72538bdcf7e48abe0a3396b6a54e0e0569` (handover PR #3).
- Niobium source inspection baseline: `088925b1c997737ab743ffea5f8a01a614dbffb9`. Relevant APIs include GenericDomain, GenericDomainRepository, IRepository, entity mapping, CloudTableRepository, explicit Platform.StorageTable composition and platform HTTP redirect configuration.
- Verified references: Niobium.Abstractions 4.2.66; Niobium.Database.StorageTable explicitly pinned to 4.2.66; Niobium.Platform.Functions and Niobium.Platform.StorageTable 4.2.70; Microsoft.Extensions.Options 10.0.0 in Core.
- Native MCP extension 1.6.0 and typed-result SDK 1.0.0-preview.4. The preview dependency is a recorded release risk, not hidden. Its real local host wire probe passed for the implementation commit below.
- Authentication: single-tenant Entra delegated RS256 access tokens, independently validated from the transport bearer. No assumed reuse of ordinary HTTP authentication middleware and no trust in unverified platform identity headers.
- Optional conversation URL metadata is not consumed. Known 202 headers establish submission acceptance even with an unusable body.

Official reference surfaces reviewed: [Workspace Agent trigger guide](https://developers.openai.com/workspace-agents/trigger-runs), [Azure MCP trigger documentation](https://learn.microsoft.com/azure/azure-functions/functions-bindings-mcp-tool-trigger), and [Azure MCP extension implementation](https://github.com/Azure/azure-functions-mcp-extension). The typed dictionary converter can coerce numbers/date-like strings and case-distinct keys, so dispatch uses original binding JSON. Moving documentation/source URLs are not runtime proof.

## Observed local runtime compatibility

Implementation commit `7320e082b2d2f1aa5cd48c45c03e49482acbb23f` passed [verification job 137580687829](https://github.com/niobium-nz/Niobium.AI.Agent.Driver/actions/runs/45141602121/job/137580687829). Runtime: .NET SDK 10.0.404, Functions Core Tools 4.17.0 and Azurite 3.35.0. The resolved package graph, host logs, wire result and coverage XML are in artifact `local-test-evidence-7320e082b2d2f1aa5cd48c45c03e49482acbb23f`.

Observed: 68 unit tests and 3 integration tests passed; both Bicep templates compiled; the real native host negotiated MCP 2025-06-18, exposed exactly list_agents/dispatch_agent, did not mark dispatch read-only, and returned matching structured/text objects for disabled-mode calls. Coverage was 93.50% combined, 100% Contracts, 100% Core and 86.80% Server, with unchanged thresholds and no authored exclusions.

The real pinned Table adapter round-tripped keys and ETags, enforced a stale conditional write with Azure RequestFailedException status 412, and applied current grant policy. Composition inspection verified the registered actual repositories and non-redirecting outbound transport without extra retry/logging handlers. Niobium's static module registration guards require a shared integration-test host; this matches the single production host per process rather than resetting private framework state in tests.

A disabled local wire probe does not prove successful authenticated dispatch, real OAuth discovery, an Azure ingress boundary or upstream acceptance. Subsequent PR heads require their own delivery checks even when only documentation changes.

## Remaining evidence for activation

| Gate | Existing evidence | Still required |
| --- | --- | --- |
| Dependency/build | Clean restore and successful exact implementation-commit builds; package graph artifact | Final-head verification and review of recorded preview/obsolete API warnings. |
| Domain/submission | Passing tests for revocation/deletion, ETags, deterministic JSON and retry uncertainty | Preserve coverage and regressions on the final head. |
| Real persistence | Passing local actual-adapter tests against Azurite | Approved Azure environment/RBAC/Key Vault acceptance. |
| Native MCP | Passing local initialize/tools-list/disabled-call probe | Actual authenticated ChatGPT connection and consequential-action approval behavior. |
| Deployed OAuth | Mandatory-auth Bicep compiles; signed JWT positive/negative unit tests pass | Actual discovery/client/redirect flow, all ingress/bypass paths, valid/invalid token and spoofed-header observations. |
| Upstream acceptance | Fixed-origin transport and fault-injection tests pass | Authorized harmless nonproduction trigger, independent B receipt and same-key retry observation. |
| Operations/privacy | Disabled defaults and safe application logging tests | Platform telemetry redaction, restrictive CORS, authenticated health probes and deployment acceptance. |

The authoring container had no .NET SDK and could not resolve github.com for cloning. Changes used the connected GitHub API; verification above was actually executed by GitHub Actions, not claimed as local authoring-environment execution. No live provider token was used, no agent run was requested, and no production activation or merge was performed.
