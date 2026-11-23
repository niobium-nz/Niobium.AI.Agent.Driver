using System.Text.Json;
using System.Text.Json.Serialization;

namespace Niobium.AI.Agent.Driver.Contracts;

/// <summary>Stable wire representation shared by MCP results and operator configuration.</summary>
public static class GatewayJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 40
    };
}

/// <summary>A logical handoff. Retries must preserve the target, ID and complete input.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DispatchAgentRequest
{
    [JsonPropertyName("target_agent")]
    public required string TargetAgent { get; init; }
    [JsonPropertyName("handoff_id")]
    public required string HandoffId { get; init; }
    [JsonPropertyName("input")]
    public required HandoffInput Input { get; init; }
    [JsonPropertyName("conversation_key")]
    public string? ConversationKey { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HandoffInput
{
    [JsonPropertyName("task")]
    public required string Task { get; init; }
    [JsonPropertyName("data")]
    public JsonElement Data { get; init; } = JsonSerializer.SerializeToElement(new Dictionary<string, object>());
}

[JsonConverter(typeof(JsonStringEnumConverter<DispatchState>))]
public enum DispatchState
{
    [JsonStringEnumMemberName("accepted")] Accepted = 0,
    [JsonStringEnumMemberName("not_dispatched")] NotDispatched = 1,
    [JsonStringEnumMemberName("rejected")] Rejected = 2,
    [JsonStringEnumMemberName("outcome_unknown")] OutcomeUnknown = 3
}

[JsonConverter(typeof(JsonStringEnumConverter<RetryAdvice>))]
public enum RetryAdvice
{
    [JsonStringEnumMemberName("none")] None = 0,
    [JsonStringEnumMemberName("retry_same_event_only")] RetrySameEventOnly = 1,
    [JsonStringEnumMemberName("operator_action_required")] OperatorActionRequired = 2
}

[JsonConverter(typeof(JsonStringEnumConverter<GatewayError>))]
public enum GatewayError
{
    [JsonStringEnumMemberName("invalid_request")] InvalidRequest = 1,
    [JsonStringEnumMemberName("input_too_large")] InputTooLarge = 2,
    [JsonStringEnumMemberName("unauthenticated")] Unauthenticated = 3,
    [JsonStringEnumMemberName("forbidden")] Forbidden = 4,
    [JsonStringEnumMemberName("gateway_disabled")] GatewayDisabled = 5,
    [JsonStringEnumMemberName("target_unavailable")] TargetUnavailable = 6,
    [JsonStringEnumMemberName("configuration_unavailable")] ConfigurationUnavailable = 7,
    [JsonStringEnumMemberName("deadline_exceeded")] DeadlineExceeded = 8,
    [JsonStringEnumMemberName("upstream_rejected")] UpstreamRejected = 9,
    [JsonStringEnumMemberName("upstream_configuration")] UpstreamConfiguration = 10,
    [JsonStringEnumMemberName("rate_limited")] RateLimited = 11,
    [JsonStringEnumMemberName("upstream_outcome_unknown")] UpstreamOutcomeUnknown = 12,
    [JsonStringEnumMemberName("registry_conflict")] RegistryConflict = 13,
    [JsonStringEnumMemberName("registry_limit_exceeded")] RegistryLimitExceeded = 14
}

/// <summary>Acceptance is acknowledgment of submission, never task completion.</summary>
public sealed record DispatchAgentResult(
    string TargetAgent,
    string HandoffId,
    DispatchState DispatchState,
    string? AcceptedBy = null,
    string? ConversationUrl = null,
    GatewayError? ErrorCode = null,
    RetryAdvice RetryAdvice = RetryAdvice.None);

public sealed record AgentDescriptor(string TargetAgent, string DisplayName, string Description);
public sealed record ListAgentsResult(IReadOnlyList<AgentDescriptor> Agents);
public sealed record GatewayToolError(GatewayError ErrorCode, RetryAdvice RetryAdvice);
