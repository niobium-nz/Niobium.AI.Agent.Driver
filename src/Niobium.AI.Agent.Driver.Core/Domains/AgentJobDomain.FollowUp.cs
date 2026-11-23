using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Core.Domains;

public sealed partial class AgentJobDomain
{
    public Task<AgentJob?> ClaimObservationAsync(string owner, DateTimeOffset now, CancellationToken token)
    {
        RequireOwner(owner);
        return ChangeFollowUpAsync(row => row.CanObserve(now) ? row with
        { PollLeaseOwner = owner, PollLeaseUntilUtc = now.AddSeconds(60), PollGeneration = checked(row.PollGeneration + 1) } : null, token);
    }

    public async Task RecordObservationAsync(AgentJob claim, ProviderObservation observation, DateTimeOffset now, CancellationToken token)
    {
        if ((observation.State is JobProviderState state && !Enum.IsDefined(state)) || !Enum.IsDefined(observation.Error))
        { throw new JobException(JobError.InvalidRequest); }
        _ = await ChangeFollowUpAsync(row => SamePollClaim(row, claim, now) ? row with
        {
            ProviderStateCode = observation.State is JobProviderState observed ? (int)observed : row.ProviderStateCode,
            ProviderErrorCode = (int)observation.Error, LastPolledUtc = now,
            NextPollUtc = now.AddSeconds(Math.Clamp(observation.RetryAfterSeconds, 60, 3600)),
            PollLeaseOwner = null, PollLeaseUntilUtc = null
        } : null, token);
    }

    public Task<AgentJob?> ClaimContinuationAsync(string owner, DateTimeOffset now, int leaseSeconds, CancellationToken token)
    {
        RequireOwner(owner);
        if (leaseSeconds is < 60 or > 300) { throw new JobException(JobError.InvalidRequest); }
        return ChangeFollowUpAsync(row => row.CanNotify(now) ? row with
        {
            ContinuationStateCode = (int)JobContinuationState.Sending, ContinuationFirstAttemptUtc = now,
            ContinuationLeaseOwner = owner, ContinuationLeaseUntilUtc = now.AddSeconds(leaseSeconds),
            ContinuationGeneration = checked(row.ContinuationGeneration + 1)
        } : null, token);
    }

    public async Task RecordContinuationAsync(AgentJob claim, DispatchAgentResult outcome, DateTimeOffset now, CancellationToken token)
    {
        _ = await ChangeFollowUpAsync(row => SameContinuationClaim(row, claim, now) ? row with
        {
            ContinuationStateCode = (int)(outcome.DispatchState switch
            {
                DispatchState.Accepted => JobContinuationState.Accepted,
                DispatchState.OutcomeUnknown => JobContinuationState.Unknown,
                _ => JobContinuationState.Held
            }),
            ContinuationErrorCode = outcome.ErrorCode is GatewayError error ? (int)error : null,
            ContinuationLeaseOwner = null, ContinuationLeaseUntilUtc = null
        } : null, token);
    }

    public async Task HoldPendingContinuationAsync(CancellationToken token)
    {
        _ = await ChangeFollowUpAsync(row => row.ContinuationStateCode == (int)JobContinuationState.Pending ? row with
        { ContinuationStateCode = (int)JobContinuationState.Held, ContinuationErrorCode = (int)GatewayError.Forbidden } : null, token);
    }

    private static bool SamePollClaim(AgentJob row, AgentJob claim, DateTimeOffset now) => row.JobId == claim.JobId
        && row.WorkspaceShard == claim.WorkspaceShard && row.PollLeaseOwner is not null && row.PollLeaseOwner == claim.PollLeaseOwner
        && row.PollGeneration == claim.PollGeneration && row.PollLeaseUntilUtc > now && row.ProviderRunId == claim.ProviderRunId
        && !JobFollowUp.IsTerminalProvider(row.ProviderStateCode) && row.ResultStateCode != (int)JobResultState.Purged;

    private static bool SameContinuationClaim(AgentJob row, AgentJob claim, DateTimeOffset now) => row.JobId == claim.JobId
        && row.WorkspaceShard == claim.WorkspaceShard && row.ContinuationStateCode == (int)JobContinuationState.Sending
        && row.ContinuationLeaseOwner is not null && row.ContinuationLeaseOwner == claim.ContinuationLeaseOwner
        && row.ContinuationGeneration == claim.ContinuationGeneration && row.ContinuationLeaseUntilUtc > now;

    private static void RequireOwner(string owner)
    { if (!Guid.TryParseExact(owner, "N", out _)) { throw new JobException(JobError.InvalidRequest); } }

    private async Task<AgentJob?> ChangeFollowUpAsync(Func<AgentJob, AgentJob?> change, CancellationToken token)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            AgentJob current = await ReadCurrentAsync(token) ?? throw new JobException(JobError.JobUnavailable);
            AgentJob? next = change(current);
            if (next is null) { return null; }
            AgentJob? saved = await TryPersistAsync(next, false, token);
            if (saved is not null) { return saved; }
        }
        throw new JobException(JobError.StorageConflict);
    }
}
