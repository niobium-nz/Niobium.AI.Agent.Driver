# Agent Gateway implementation progress

## Baseline and scope

Stage 1 source was merged in PR #5 as `a2b701b9e98f500092a27ac2ac8acaef2ce4ca44`. Its tested feature head was `40c8a5f8867459f2ecd7e988f2782ab0b019653f`. The follow-on implementation is PR #6 on `feature/agent-job-results`. It adds the disabled store-only Stage 2 path without treating the merge of Stage 1 as proof of live OAuth or upstream acceptance.

| Original work package / gate | Current state |
| --- | --- |
| Stage 1 source, native MCP, current authorization, bounded provider dispatch | Merged; legacy dispatch remains persistence-free. |
| Stage 1 live OAuth and A-to-B acceptance | Still required. No live provider credentials or API channel were supplied or invented in this implementation session. |
| S2.1 tracked registration and dispatch | Implemented source: explicit grants, keyed identity, immutable fingerprint, conditional create/claim, conservative ambiguous outcomes. |
| S2.2 result exchange | Implemented source: application-owned result schema, OAuth plus scoped JWT capability, immutable duplicate-safe result, owner-only reads. |
| S2.3 required maintenance | Implemented source: bounded checkpointed shard scan, cursor lease, never-attempted recovery, expired-send holds, deadlines and physical retention cleanup. |
| S2.3 optional provider polling | Not implemented. Provider state stays unobserved. No guessed status API is called. |
| S2.4 optional parent continuation | Not implemented. Store-only is the sole supported completion mode. No route entity or notification send is exposed. |
| S2.5 activation and production acceptance | Runbooks and disabled staging parameters authored; real OAuth, privacy, approved credentials/resources and end-to-end trials remain gated. |

## New source and safety boundaries

Four additive MCP tools: `start_agent_job`, `submit_job_result`, `get_job_status`, `get_job_result`. `AgentJobDomain` owns conditional state changes and immutable result rules; `AgentJobFlow` coordinates current grants and the existing bounded provider client. `AgentMaintenanceDomain` owns sweep leases/checkpoints and `AgentJobMaintenanceFlow` performs bounded work. Server functions remain transport adapters.

Only version-2 registrations and explicit version-2 grants permit tracked work. Version-1 rows preserve Stage 1 behavior without gaining job permissions. Identity and signing keys are dedicated, allowlisted and resolved from configuration. Raw completion capabilities never enter stored snapshots or initiator responses. A valid callback can precede the dispatch acknowledgment save. Unknown/Held dispatches are never automatically resent or purged by the timer.

Default deployments create only the existing two registry tables and disable all execution flags, including the new timer. The separate tracked-job parameter file stages additional tables only when explicitly selected. No production activation, live agent trigger, provider polling, continuation or package publication is part of PR #6.

## Verification record

Historical Stage 1 feature run [45147173401](https://github.com/niobium-nz/Niobium.AI.Agent.Driver/actions/runs/45147173401) passed 73 unit tests, 5 integration tests, test deployment and the trusted main harness, with 93.56% combined coverage. Those results do not certify the Stage 2 changes.

The initial Stage 2 commit `c4231c2af555d9114bc2c9f47261e5c41c16902d` built and passed its unit/local integration tests in run [45153340794](https://github.com/niobium-nz/Niobium.AI.Agent.Driver/actions/runs/45153340794), but failed the unchanged coverage gate. Additional capability, maintenance, tool-boundary and real-adapter concurrency/purge tests were added. One subsequent compile finding in the new test fixture (TableEntity enumeration) was corrected rather than bypassed.

Consult [PR #6](https://github.com/niobium-nz/Niobium.AI.Agent.Driver/pull/6) and its exact final-head run for current terminal test/coverage/deployment results. Keep failed, skipped, old-head and authored-only checks separate from evidence. The trusted pipeline files and 90% combined / 80% per-module thresholds are unchanged. The authoring container has no .NET SDK; .NET and Azure results must come from actual GitHub Actions execution, not a claimed local build.

## Activation and deferred work

Before enabling either dispatch path: establish actual ChatGPT OAuth discovery/redirect/refresh and delegated identity compatibility; prove mandatory authentication on all reachable paths; restrict CORS; authenticate health probes; verify platform telemetry excludes bodies/capabilities/bearers; authorize a harmless nonproduction end-to-end trial and independently observe receipt. Before tracked activation, also stage approved tables, dedicated stable identity/signing keys, version-2 registrations/grants and callback instructions.

Optional provider polling and parent continuation need their own reviewed implementation and acceptance. Additional result-reader policies, a large-scale indexed scheduler, operator resolution of Held jobs, and identity-key migration are not claimed implemented. See [tracked jobs](tracked-jobs.md), [compatibility evidence](compatibility-evidence.md), and the original handover for the remaining work.
