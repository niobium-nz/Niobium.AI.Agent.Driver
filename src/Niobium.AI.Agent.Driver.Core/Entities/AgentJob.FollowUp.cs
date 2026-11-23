using System.Text.Json;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Dispatch;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Core.Entities;

public sealed partial record AgentJob
{
    public bool ObserveProviderRun { get; set; }
    public string? ProviderRunId { get; set; }
    public DateTimeOffset? LastPolledUtc { get; set; }
    public DateTimeOffset? NextPollUtc { get; set; }
    public int ProviderErrorCode { get; set; }
    public string? PollLeaseOwner { get; set; }
    public DateTimeOffset? PollLeaseUntilUtc { get; set; }
    public long PollGeneration { get; set; }
    public int CompletionModeCode { get; set; }
    public string? ContinuationRef { get; set; }
    public string? ParentTargetAgent { get; set; }
    public string? ParentApiChannelId { get; set; }
    public string? ParentCredentialAlias { get; set; }
    public string? ParentConversationKey { get; set; }
    public DateTimeOffset? ContinuationExpiresUtc { get; set; }
    public string? ContinuationLeaseOwner { get; set; }
    public DateTimeOffset? ContinuationLeaseUntilUtc { get; set; }
    public long ContinuationGeneration { get; set; }
    public DateTimeOffset? ContinuationFirstAttemptUtc { get; set; }
    public int? ContinuationErrorCode { get; set; }

    private void RequireFollowUpValid()
    {
        if (!Enum.IsDefined((JobProviderState)ProviderStateCode) || !Enum.IsDefined((JobContinuationState)ContinuationStateCode)
            || !Enum.IsDefined((JobCompletionMode)CompletionModeCode) || !Enum.IsDefined((ProviderObservationError)ProviderErrorCode)
            || PollGeneration < 0 || ContinuationGeneration < 0
            || (ProviderRunId is not null && (!ObserveProviderRun || !JobFollowUp.IsRunId(ProviderRunId)))
            || (ProviderStateCode != 0 && ProviderRunId is null)
            || (Version == 1 && (ObserveProviderRun || CompletionModeCode != 0 || ProviderStateCode != 0 || ContinuationStateCode != 0)))
        { throw new JobException(JobError.JobUnavailable); }
        if (CompletionModeCode == (int)JobCompletionMode.StoreOnly)
        {
            if (ContinuationRef is not null || ParentTargetAgent is not null || ParentApiChannelId is not null
                || ParentCredentialAlias is not null || ParentConversationKey is not null || ContinuationExpiresUtc is not null
                || ContinuationStateCode != 0 || ContinuationFirstAttemptUtc is not null)
            { throw new JobException(JobError.JobUnavailable); }
        }
        else if (Version != 2 || !IdentifierRules.IsAlias(ContinuationRef) || !IdentifierRules.IsAlias(ParentTargetAgent)
            || !IdentifierRules.IsChannel(ParentApiChannelId) || !IdentifierRules.IsAlias(ParentCredentialAlias)
            || ContinuationExpiresUtc is null || ContinuationExpiresUtc <= CreatedUtc
            || (ResultStateCode != (int)JobResultState.Purged && !IdentifierRules.IsEventId(ParentConversationKey))
            || (ContinuationStateCode != 0 && ResultStateCode is not ((int)JobResultState.Available) and not ((int)JobResultState.Purged)))
        { throw new JobException(JobError.JobUnavailable); }
    }

    public bool CanObserve(DateTimeOffset now) => Version == 2 && ObserveProviderRun && ProviderRunId is not null
        && DispatchStateCode == (int)JobDispatchState.Accepted && !JobFollowUp.IsTerminalProvider(ProviderStateCode)
        && ResultStateCode != (int)JobResultState.Purged && now < CapabilityExpiresUtc
        && (NextPollUtc is null || NextPollUtc <= now) && (PollLeaseUntilUtc is null || PollLeaseUntilUtc <= now);

    public bool CanNotify(DateTimeOffset now) => Version == 2 && CompletionModeCode == (int)JobCompletionMode.NotifyParent
        && ResultStateCode == (int)JobResultState.Available && ContinuationStateCode == (int)JobContinuationState.Pending
        && ContinuationFirstAttemptUtc is null && ContinuationExpiresUtc > now;

    public PreparedDispatch PrepareContinuation(int maxBytes)
    {
        RequireValid();
        if (CompletionModeCode != (int)JobCompletionMode.NotifyParent || ResultStateCode != (int)JobResultState.Available)
        { throw new JobException(JobError.JobUnavailable); }
        string key = StableIdentity.Derive("niobium-job-continuation-v1", WorkspaceKey, JobId,
            ContinuationRef!, ParentApiChannelId!, ParentConversationKey!);
        string input = JsonSerializer.Serialize(new
        { schema = "niobium.agent-result-ready.v1", event_id = key, job_id = JobId, result_hash = ResultHash }, GatewayJson.Options);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { input, conversation_key = ParentConversationKey }, GatewayJson.Options);
        if (body.Length > maxBytes) { throw new JobException(JobError.InputTooLarge); }
        return new(ParentTargetAgent!, key, ParentApiChannelId!, key, body);
    }
}
