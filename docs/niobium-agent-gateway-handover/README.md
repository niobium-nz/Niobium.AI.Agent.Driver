# Niobium.AgentGateway - Codex handover package

This is an implementation plan, not an implemented Function App or an installable ChatGPT agent package.

Start with `Niobium.AgentGateway.Implementation-Plan.md`. Give Codex that file together with `CODEX-KICKOFF.md` and access to the selected destination repository plus the three read-only reference repositories.

The package contains:

- `Niobium.AgentGateway.Implementation-Plan.md`: complete two-stage design, repository alignment, contracts, failure handling, Azure setup, work packages and acceptance criteria.
- `CODEX-KICKOFF.md`: ready-to-use implementation instruction, Stage 1 only initially.
- `contracts/`: proposed JSON Schema inputs/outputs and example payloads. They describe the gateway, not the upstream ChatGPT API. They are not executable MCP bindings.
- `reference-manifest.json`: inspected repository revisions, paths and official platform sources to recheck.
- `VALIDATION.md`: local document/schema package checks, with clear limits on what was not tested.

Stage 1 only dispatches one-way handoffs. Its application tables contain agent registrations and access grants, not per-handoff execution state. Stage 2 separately adds tracked results and optional parent continuation.

No repository was modified and no Azure infrastructure or ChatGPT agent was deployed while preparing this package. No live credentials are included.
