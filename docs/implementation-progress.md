# Implementation progress

## Baselines and delivery

Stage 1 PR #5 was merged to `main` as `a2b701b9e98f500092a27ac2ac8acaef2ce4ca44`. Its merge-triggered production deployment and read-only checks passed. No live dispatch activation was implied.

Stage 2 is being delivered in PR #6 on `feature/agent-job-results`. The store-only baseline `d24e8a002b81a8bfca2efdb0a0ef5127c343623a` passed 109 unit tests, 8 local integration tests, 95.17% combined coverage, packaging, exact-commit test deployment and independent health verification in Actions run 45154757229. This historical evidence does not certify later commits.

The follow-up commit `93a24949053cd5ec447c20b678bf2dca333a1f46` adds optional provider observation and parent notification. Its build, unit/integration tests and unchanged coverage gate passed in run 45162456012 while the next activation-probe/CORS changes were being prepared. The final PR description records the terminal exact-head results; no local .NET execution in the authoring container is claimed.

## Original plan coverage

| Work package | Source status | Acceptance boundary |
| --- | --- | --- |
| S1.1 compatibility/authentication | Native MCP, validated Entra caller adapter, mandatory-auth template and read-only acceptance probe implemented | Actual ChatGPT OAuth discovery/client/redirect/refresh and all deployed ingress checks remain outstanding |
| S1.2-S1.4 core dispatch | Registry/grants, deterministic HTTP policy, native tools and setup instructions implemented | Harmless live A-to-B trial requires approved agents, credentials and explicit execution authorization |
| S1.5 infrastructure/operations | Existing trusted pipeline retained, defaults disabled, restrictive CORS default, operating guides and independent health evidence | Applying mandatory OAuth requires a reviewed authenticated-harness transition and real telemetry privacy evidence |
| S2.1-S2.2 tracked result exchange | Durable identity, conditional state, completion capabilities, duplicate-safe results, owner reads implemented | Live A-to-B-to-result trial and issued-job rollback verification remain environment-specific |
| S2.3 maintenance and optional polling | Bounded persisted sweeps, leases, deadlines, retention and opt-in run-status observer implemented | Beta API compatibility must be demonstrated with an approved test run; missing IDs do not trigger redispatch |
| S2.4 optional parent continuation | Operator routes, atomic same-row result/outbox, deterministic notification and fenced claims implemented | Actual API-conversation routing trial remains required; arbitrary ChatGPT UI-thread resume is not supported |
| S2.5 failure tests/rollout | Added concurrency, recovery, rollback, authorization, HTTP and real Table tests | Independent approval plus final live acceptance are not replaced by unit tests or disabled deployments |

## Additional source improvements

A review finding in the store-only baseline exposed the case where short retention could purge a result while its callback capability remained valid. Cleanup now waits for capability expiry as well as terminal disposition and retention. A duplicate during that period receives its original receipt. Pending or uncertain parent notifications also prevent automatic payload purge.

`AgentJob` version 2 adds optional observation and continuation fields without changing the version-1 store-only contract. No automatic backfill is needed. New follow-up flags default off; ordinary IaC also pins them off. The parent route table is only in explicit storage-staging parameters. No model-facing arbitrary URL, channel, token or conversation-binding operation was added.

See [job follow-ups](job-follow-ups.md) for optional contract/state/rollback details and [protected gateway acceptance](protected-gateway-acceptance.md) for the read-only operator probe. The new probe does not modify the trusted readiness controller or fabricate any success status. Missing live token prerequisites are reported as skipped and cause failure.

## Still not performed or claimed

No production activation, agent publication, workspace permission change, live billable agent trigger, live result submission, live continuation binding, new application queue or package publication was performed. Real tenant/client configuration and approved credentials are not values to invent in source. Telemetry must be inspected on the actual deployed platform. Keep callbacks/readers on the updated binary when rolling back version-2 starts; disabling flags is not equivalent to deploying an older binary that cannot read version 2.

Additional result-reader policies, automatic operator resolution of ambiguous jobs, identity-key migration and high-scale indexed scheduling remain separately reviewable operational extensions, not prerequisites for this bounded initial implementation.
