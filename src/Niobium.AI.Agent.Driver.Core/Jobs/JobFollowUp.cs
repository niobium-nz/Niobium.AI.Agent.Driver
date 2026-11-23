using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Dispatch;

namespace Niobium.AI.Agent.Driver.Core.Jobs;

/// <summary>Observation never substitutes for a callback or authorizes a new send.</summary>
public enum JobProviderState
{
    Unobserved = 0, Queued = 1, InProgress = 2, Suspended = 3, Completed = 4, Failed = 5, Unrecognized = 6
}

public enum JobContinuationState
{
    NotRequested = 0, Pending = 1, Sending = 2, Accepted = 3, Unknown = 4, Held = 5
}

public enum ProviderObservationError
{
    None = 0, Unavailable = 1, InvalidResponse = 2, DeadlineExceeded = 3,
    RateLimited = 4, AccessDenied = 5, DispatchFailed = 6, RunFailed = 7
}

public sealed record TrackedDispatchResult(DispatchAgentResult Outcome, string? RunId = null);
public sealed record ProviderObservation(JobProviderState? State, ProviderObservationError Error = ProviderObservationError.None,
    int RetryAfterSeconds = 60);

/// <summary>Optional beta contract. Ordinary Stage 1 dispatch does not request or consume run metadata.</summary>
public interface IWorkspaceAgentRunClient : IWorkspaceAgentClient
{
    Task<TrackedDispatchResult> DispatchTrackedAsync(PreparedDispatch request, string credential, CancellationToken cancellationToken);
    Task<ProviderObservation> ObserveAsync(string channel, string runId, string credential, CancellationToken cancellationToken);
}

public static class JobFollowUp
{
    public static bool IsRunId(string? id) => id is { Length: > 7 and <= 160 }
        && id.StartsWith("apirun_", StringComparison.Ordinal)
        && id.AsSpan(7).ToArray().All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static bool IsTerminalProvider(int code) => code is (int)JobProviderState.Completed or (int)JobProviderState.Failed;

    public static string ProviderName(int code) => (JobProviderState)code switch
    {
        JobProviderState.Unobserved => "unobserved", JobProviderState.Queued => "queued",
        JobProviderState.InProgress => "in_progress", JobProviderState.Suspended => "suspended",
        JobProviderState.Completed => "completed", JobProviderState.Failed => "failed",
        JobProviderState.Unrecognized => "unrecognized", _ => throw new JobException(JobError.JobUnavailable)
    };

    public static string ContinuationName(int code) => (JobContinuationState)code switch
    {
        JobContinuationState.NotRequested => "not_requested", JobContinuationState.Pending => "pending",
        JobContinuationState.Sending => "sending", JobContinuationState.Accepted => "accepted",
        JobContinuationState.Unknown => "unknown", JobContinuationState.Held => "held",
        _ => throw new JobException(JobError.JobUnavailable)
    };
}
