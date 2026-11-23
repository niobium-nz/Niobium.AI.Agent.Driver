# Proposed gateway contracts

These JSON Schemas describe the MCP gateway tool arguments/results, not the upstream Workspace Agents API and not executable Azure Functions bindings. The `.invalid` schema IDs are identifiers only; resolve references locally.

`input` is a structured object at the gateway boundary. The server serializes its complete versioned task envelope into the upstream API's **string** `input` field.

JSON Schema validation does not enforce authorization, stable-event semantics, aggregate nested-property count, canonical serialized byte limits, callback signature verification, allowed business-result schemas or Table size budgets. Implement those checks in application/domain code and test them separately.

Stage 1 is limited to `list_agents` and `dispatch_agent`. Stage 2 schema files are design inputs for the later stage, not permission to implement it now. Stage 2 status/acknowledgment DTOs still need finalization against the state model in the main plan.

The example completion capability is deliberately invalid. The example hash is a placeholder, not a calculated assertion of integrity.
