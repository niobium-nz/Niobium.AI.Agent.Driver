# Compatibility evidence and activation gates

Evidence status must distinguish source inspection, authored tests and tests actually executed. The implementation does not claim a live ChatGPT connection or a successful Workspace Agent trigger.

## Reviewed baseline and decisions

- Driver baseline: `56c92c72538bdcf7e48abe0a3396b6a54e0e0569` (handover PR #3).
- Niobium framework source inspection baseline recorded during implementation: `088925b1c997737ab743ffea5f8a01a614dbffb9`. Relevant APIs: GenericDomain, GenericDomainRepository, IRepository, entity key mapping, CloudTableRepository, explicit Platform.StorageTable composition and platform HTTP redirect configuration.
- Selected references: Niobium.Abstractions 4.2.66; Niobium.Platform.Functions and Niobium.Platform.StorageTable 4.2.70. These choices require a clean resolved-graph build; source compatibility is not a substitute.
- Native MCP extension: 1.6.0. Typed-result SDK: 1.0.0-preview.4. The preview dependency is a recorded compatibility risk, not hidden. The real host wire probe must pass with the resolved packages/runtime before readiness.
- Authentication: single-tenant Entra delegated RS256 access tokens, independently validated from the transport bearer. No assumed reuse of ordinary HTTP authentication middleware; no trust in unverified platform headers.
- Optional conversation URL metadata is deliberately not consumed. Known 202 headers always establish submission acceptance, even with an unusable body.

The official reference surfaces reviewed are the [Workspace Agent trigger guide](https://developers.openai.com/workspace-agents/trigger-runs), [Azure MCP trigger documentation](https://learn.microsoft.com/azure/azure-functions/functions-bindings-mcp-tool-trigger), and the [Azure MCP extension implementation](https://github.com/Azure/azure-functions-mcp-extension). In particular, the typed dictionary converter coerces numbers/date-like strings and case-distinct keys, so dispatch reads original binding JSON. Pin runtime evidence, not just links to a moving main branch.

## Evidence to collect for the exact candidate commit

| Gate | Authored verification | Evidence required before activation |
| --- | --- | --- |
| Dependency graph/build | CI restore/build; package graph artifact | Successful exact-commit job, no ignored downgrade/restore errors |
| Domain and submission behavior | Unit tests including revocation, ETags, deterministic JSON and retry uncertainty | Passing test output and unchanged coverage threshold report |
| Real persistence | AzuriteRegistryTests with actual pinned CloudTableRepository | Key/ETag round-trip, stale service ETag failure, expiry and revocation results |
| Native MCP | Local initialize/tools-list/disabled-call wire probe | Structured object and matching text result; only two tools; no read-only dispatch annotation |
| Deployed OAuth | Mandatory-auth Bicep template plus application JWT tests | Actual ChatGPT OAuth/discovery flow, ingress/bypass checks, valid/invalid token and spoofed-header observations |
| Upstream acceptance | Fixed provider integration plus fault-injection unit tests | Authorized harmless nonproduction trigger, independent B receipt, same-key retry observation |
| Operations/privacy | Disabled defaults and safe application logs | Key Vault/RBAC resolution, telemetry redaction and authenticated health acceptance |

This authoring environment has no .NET SDK and could not resolve github.com for a git clone. GitHub connector writes were used instead. No local build, test, Bicep compilation or coverage percentage is asserted. The PR and workflow artifacts are the authoritative record of subsequently observed CI results. OAuth/live-provider gates remain blocked until an authorized operator supplies and verifies the real environment; unit mocks and local host tests cannot certify them.
