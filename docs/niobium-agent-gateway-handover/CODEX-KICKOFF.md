# Codex implementation brief - Niobium.AgentGateway

Read `Niobium.AgentGateway.Implementation-Plan.md` before making changes. This package is a plan with proposed contracts, not a working implementation.

## Objective

Create an Azure Function App that exposes a thin MCP gateway for ChatGPT Workspace Agents. Agent A calls the gateway; the gateway triggers a published Agent B through the ChatGPT Workspace Agents API. The AI agents remain hosted in ChatGPT, not Codex. Codex is only the implementation tool.

**Implement Stage 1 only initially.** Stage 2 is a separately authorized follow-on after Stage 1 acceptance.

## Stage 1 boundary

Expose `list_agents` and `dispatch_agent`. Authenticate and authorize callers, validate the request, submit a bounded HTTP trigger request, await ChatGPT's dispatch acknowledgment and return. Do not wait for B to perform the task.

Use Azure Table service for `AgentRegistration` and `AgentGrant` configuration only. Do not create per-handoff/job records, input archives, status tracking, polling, callbacks, result storage, queues, timers, Durable Functions or an external orchestration engine in Stage 1. No detached background work after the Function returns.

## Repository pattern

Use these as read-only references, rechecking the current files and resolved package graph:

- `niobium-nz/Niobium.Invoicing`: bootstrap, .NET 10 isolated Functions, Contracts/Core/Server split, generic infrastructure.
- `niobium-nz/Niobium.Store`: rich domains, flow/repository boundaries, explicit platform Table DI registration.
- `niobium-nz/Niobium.Notification`: typed HTTP clients, explicit retry ownership, redirect configuration, safe ETag handling and delivery verification.

Read destination `AGENTS.md`, linked domain/data-access and delivery documents, current branch/SHA and working-tree state. Preserve unrelated work. Do not alter the reference services or framework just to implement the gateway. Follow package references rather than replacing them with cross-repository source projects.

Business decisions belong on Niobium domains; flows coordinate; adapters own SDK access. Do not put `TableClient` or `IAzureTableClientFactory` in business flows. Use explicit finite enum codes, named results for I/O before branching, and ETag-conditional updates. Do not use force-save as a concurrency mechanism.

## First milestone: prove compatibility

Use native Azure Functions MCP support and Streamable HTTP. Test actual MCP initialization/discovery/calls, .NET 10 package compatibility, authenticated caller context and the real ChatGPT OAuth connection. A VS Code sample alone does not prove ChatGPT interoperability.

Production inbound auth should use a supported Entra OAuth arrangement and mandatory Function endpoint protection. Do not replace OAuth with a URL API key or a public anonymous endpoint. Do not assume existing HTTP bearer middleware applies to the native MCP trigger. Validate trusted principal extraction and spoof-header rejection.

The upstream uses a server-held Workspace Agent access token and an `agtch_...` API-channel ID. It is not an OpenAI API Platform key or an `agt_...` ID. The provider's `input` field is a string containing the serialized gateway envelope.

## Correctness requirements

- Stable event IDs and deterministic request bytes for immediate retries.
- A known upstream HTTP 202 means accepted even if optional response metadata is absent or malformed.
- Acceptance is not task completion.
- Explicit uncertainty for a lost response; do not generate a fresh ID to retry an uncertain task.
- No hidden HTTP retries; disable redirects independently of retry handling.
- Do not import Resend-specific retry/idempotency semantics into the Workspace Agents client.
- No promise of permanent duplicate suppression or exactly-once execution in Stage 1.
- Authorize the validated principal/connection, not an agent name supplied by the model.
- No tokens, prompts, capabilities, private payloads or provider bodies in logs.

## Deliverables

Follow work packages S1.1-S1.5 in the full plan: compatibility evidence; solution/composition/configuration; provider client; MCP tools and agent setup instructions; infrastructure/CI/runbooks. Use the supplied JSON Schemas as proposed gateway contracts, not provider API schemas. Byte-size and authorization rules still require application validation.

Adopt the reference coverage thresholds of 90% combined authored code and 80% per Contracts/Core/Server module unless the owner explicitly changes them. Add unit, real-adapter Azurite, MCP host and approved deployed tests. No live financial, email or production-agent actions as smoke tests. Production verification is read-only health/version checking.

Keep `docs/implementation-progress.md` current with exact commands, results, SHA, environment and blockers. Distinguish source completion from actual deployed verification. Do not claim a skipped check passed.

## Operational authorization

The owner must identify the destination repository/checkout and approved deployment inputs. Implement locally against mocks while missing live credentials or admin setup are resolved. Do not create production resources, publish agents, change workspace permissions, seed live grants or dispatch production agents merely because the plan describes those steps. Follow explicit authorization and the destination's verified delivery policy.

Stop after the independent Stage 1 acceptance gate and report results plus the precise remaining live blockers. Do not begin Stage 2 without a follow-on instruction.

## Stage 2 follow-on summary

The later stage adds `start_agent_job`, `submit_job_result`, `get_job_status`, `get_job_result`, Table-backed job state, secure job-scoped completion capabilities, conditional claims, a bounded timer and optional allowlisted API-conversation continuation. Keep Stage 1 unchanged. Use the full plan for out-of-order callbacks, missing results, ETag write-back behavior, uncertain-send recovery, retention and continuation limitations.
