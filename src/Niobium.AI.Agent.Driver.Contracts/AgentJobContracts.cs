using System.Text.Json;
using System.Text.Json.Serialization;

namespace Niobium.AI.Agent.Driver.Contracts;

/// <summary>Job enums have fixed persisted codes and reject numeric wire input.</summary>
public sealed class StrictJobEnumConverter<T>() : JsonStringEnumConverter<T>(allowIntegerValues: false) where T : struct, Enum;

[JsonConverter(typeof(StrictJobEnumConverter<JobDispatchState>))]
public enum JobDispatchState
{
    [JsonStringEnumMemberName("registered")] Registered = 0,
    [JsonStringEnumMemberName("sending")] Sending = 1,
    [JsonStringEnumMemberName("accepted")] Accepted = 2,
    [JsonStringEnumMemberName("rejected")] Rejected = 3,
    [JsonStringEnumMemberName("unknown")] Unknown = 4,
    [JsonStringEnumMemberName("held")] Held = 5
}

[JsonConverter(typeof(StrictJobEnumConverter<JobResultState>))]
public enum JobResultState
{
    [JsonStringEnumMemberName("pending")] Pending = 0,
    [JsonStringEnumMemberName("available")] Available = 1,
    [JsonStringEnumMemberName("deadline_exceeded")] DeadlineExceeded = 2,
    [JsonStringEnumMemberName("closed_without_result")] ClosedWithoutResult = 3,
    [JsonStringEnumMemberName("purged")] Purged = 4
}

[JsonConverter(typeof(StrictJobEnumConverter<JobOutcome>))]
public enum JobOutcome
{
    [JsonStringEnumMemberName("succeeded")] Succeeded = 1,
    [JsonStringEnumMemberName("failed")] Failed = 2
}

[JsonConverter(typeof(StrictJobEnumConverter<JobCompletionMode>))]
public enum JobCompletionMode
{
    [JsonStringEnumMemberName("store_only")] StoreOnly = 0
}

[JsonConverter(typeof(StrictJobEnumConverter<JobError>))]
public enum JobError
{
    [JsonStringEnumMemberName("tracked_jobs_disabled")] TrackedJobsDisabled = 1,
    [JsonStringEnumMemberName("invalid_request")] InvalidRequest = 2,
    [JsonStringEnumMemberName("input_too_large")] InputTooLarge = 3,
    [JsonStringEnumMemberName("result_too_large")] ResultTooLarge = 4,
    [JsonStringEnumMemberName("unsupported_result_schema")] UnsupportedResultSchema = 5,
    [JsonStringEnumMemberName("job_unavailable")] JobUnavailable = 6,
    [JsonStringEnumMemberName("job_conflict")] JobConflict = 7,
    [JsonStringEnumMemberName("completion_not_authorized")] CompletionNotAuthorized = 8,
    [JsonStringEnumMemberName("completion_expired")] CompletionExpired = 9,
    [JsonStringEnumMemberName("configuration_unavailable")] ConfigurationUnavailable = 10,
    [JsonStringEnumMemberName("storage_conflict")] StorageConflict = 11,
    [JsonStringEnumMemberName("deadline_exceeded")] DeadlineExceeded = 12
}

/// <summary>Opt-in durable submission. Stage 1 dispatch_agent remains unchanged.</summary>
public sealed record StartAgentJobRequest
{
    public required string TargetAgent { get; init; }
    public required string HandoffId { get; init; }
    public required HandoffInput Input { get; init; }
    public string? ConversationKey { get; init; }
    public required string ResultSchema { get; init; }
    public required JobCompletionMode CompletionMode { get; init; }
}

public sealed record SubmitJobResultRequest
{
    public required string JobId { get; init; }
    public required string CompletionCapability { get; init; }
    public required JobOutcome Outcome { get; init; }
    public required JsonElement Result { get; init; }
}

public sealed record GetAgentJobRequest
{
    public required string JobId { get; init; }
}

/// <summary>Public projection. Never includes the request, routing, keys, leases or completion capability.</summary>
public sealed record AgentJobStatus(string JobId, string TargetAgent, string HandoffId,
    JobDispatchState DispatchState, JobResultState ResultState, string ResultSchema,
    DateTimeOffset CreatedUtc, DateTimeOffset ResultDeadlineUtc,
    string ProviderState = "unobserved", string ContinuationState = "not_requested");

public sealed record AgentJobResult(string JobId, JobResultState ResultState, string ResultSchema,
    JobOutcome? Outcome = null, JsonElement? Result = null, string? ResultHash = null,
    DateTimeOffset? ResultReceivedUtc = null, bool LateArrival = false);

public sealed record JobResultReceipt(string JobId, JobResultState ResultState, JobOutcome Outcome,
    string ResultHash, DateTimeOffset ResultReceivedUtc, bool Duplicate);

public sealed record JobToolError(JobError ErrorCode, RetryAdvice RetryAdvice);
