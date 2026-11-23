using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Core.Domains;

/// <summary>Owns durable transitions. All reads bypass cached domain snapshots and all writes are conditional replacements.</summary>
public sealed class AgentJobDomain(Lazy<IRepository<AgentJob>> repository,
    IEnumerable<IDomainEventHandler<IDomain<AgentJob>>> handlers) : GenericDomain<AgentJob>(repository, handlers)
{
    public async Task<AgentJob?> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        CheckInitialized();
        AgentJob? row = await Repository.RetrieveAsync(PartitionKey, RowKey, cancellationToken: cancellationToken);
        if (row is not null)
        {
            row.RequireValid();
            if (row.WorkspaceShard != PartitionKey || row.JobId != RowKey || string.IsNullOrEmpty(row.ETag))
            { throw new JobException(JobError.JobUnavailable); }
        }
        return row;
    }

    public async Task<AgentJob> GetForReaderAsync(CallerContext caller, CancellationToken cancellationToken)
    {
        AgentJob row = await ReadCurrentAsync(cancellationToken) ?? throw new JobException(JobError.JobUnavailable);
        if (caller.WorkspaceKey != row.WorkspaceKey || caller.PrincipalKey != row.InitiatorPrincipalKey)
        { throw new JobException(JobError.JobUnavailable); }
        return row;
    }

    public async Task<AgentJob> RegisterAsync(AgentJob intended, CancellationToken cancellationToken)
    {
        intended.RequireValid();
        CheckInitialized();
        if (intended.WorkspaceShard != PartitionKey || intended.JobId != RowKey || intended.ETag is not null
            || intended.DispatchStateCode != 0 || intended.ResultStateCode != 0 || intended.Attempts != 0
            || intended.FirstAttemptUtc is not null || intended.ResultJson is not null || intended.ClaimGeneration != 0)
        { throw new JobException(JobError.InvalidRequest); }
        for (int attempt = 0; attempt < 4; attempt++)
        {
            AgentJob? existing = await ReadCurrentAsync(cancellationToken);
            if (existing is not null)
            {
                if (existing.RequestFingerprint != intended.RequestFingerprint
                    || existing.InitiatorPrincipalKey != intended.InitiatorPrincipalKey
                    || existing.WorkspaceKey != intended.WorkspaceKey || existing.TargetAgent != intended.TargetAgent
                    || existing.HandoffId != intended.HandoffId || existing.ResultSchema != intended.ResultSchema
                    || (existing.RequestJson is not null && existing.RequestJson != intended.RequestJson))
                { throw new JobException(JobError.JobConflict); }
                return existing;
            }
            AgentJob? created = await TryPersistAsync(intended, true, cancellationToken);
            if (created is not null) { return created; }
        }
        throw new JobException(JobError.StorageConflict);
    }

    public async Task<AgentJob?> ClaimDispatchAsync(string owner, DateTimeOffset now, int leaseSeconds,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(owner, "N", out _) || leaseSeconds is < 60 or > 300)
        { throw new JobException(JobError.InvalidRequest); }
        for (int attempt = 0; attempt < 4; attempt++)
        {
            AgentJob current = await ReadCurrentAsync(cancellationToken) ?? throw new JobException(JobError.JobUnavailable);
            if (current.DispatchStateCode != (int)JobDispatchState.Registered || current.ResultStateCode != (int)JobResultState.Pending
                || current.FirstAttemptUtc is not null || current.Attempts != 0 || now >= current.ResultDeadlineUtc)
            { return null; }
            AgentJob next = current with
            {
                DispatchStateCode = (int)JobDispatchState.Sending, FirstAttemptUtc = now,
                Attempts = 1, LeaseOwner = owner, LeaseUntilUtc = now.AddSeconds(leaseSeconds),
                ClaimGeneration = checked(current.ClaimGeneration + 1)
            };
            AgentJob? claimed = await TryPersistAsync(next, false, cancellationToken);
            if (claimed is not null) { return claimed; }
        }
        throw new JobException(JobError.StorageConflict);
    }

    public async Task<AgentJob> RecordDispatchAsync(AgentJob claim, DispatchAgentResult outcome, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            AgentJob current = await ReadCurrentAsync(cancellationToken) ?? throw new JobException(JobError.JobUnavailable);
            // A lease is a fencing precondition for storage, not a guarantee about external execution.
            if (current.DispatchStateCode != (int)JobDispatchState.Sending || current.LeaseOwner != claim.LeaseOwner
                || current.ClaimGeneration != claim.ClaimGeneration || current.LeaseUntilUtc <= now
                || current.LeaseUntilUtc is null || claim.JobId != current.JobId || claim.WorkspaceShard != current.WorkspaceShard)
            { return current; }
            int state = outcome.DispatchState switch
            {
                DispatchState.Accepted => (int)JobDispatchState.Accepted,
                DispatchState.Rejected => (int)JobDispatchState.Rejected,
                DispatchState.NotDispatched => (int)JobDispatchState.Held,
                _ => (int)JobDispatchState.Unknown
            };
            // Use the freshly loaded row so a callback committed before this response is preserved.
            AgentJob next = current with { DispatchStateCode = state, LeaseOwner = null, LeaseUntilUtc = null,
                LastErrorCode = outcome.ErrorCode is GatewayError error ? (int)error : null };
            AgentJob? saved = await TryPersistAsync(next, false, cancellationToken);
            if (saved is not null) { return saved; }
        }
        throw new JobException(JobError.StorageConflict);
    }

    public async Task<JobResultReceipt> SubmitAsync(CallerContext caller, SubmitJobResultRequest request,
        IJobCapabilities capabilities, TrackedJobOptions options, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string json = JobWire.CanonicalResult(request.Outcome, request.Result, options.MaxResultBytes);
        string hash = JobWire.ResultFingerprint(request.Outcome, json);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            AgentJob current = await ReadCurrentAsync(cancellationToken) ?? throw new JobException(JobError.JobUnavailable);
            if (request.JobId != current.JobId || caller.WorkspaceKey != current.WorkspaceKey
                || caller.PrincipalKey != current.ResultWriterPrincipalKey)
            { throw new JobException(JobError.CompletionNotAuthorized); }
            if (now >= current.CapabilityExpiresUtc) { throw new JobException(JobError.CompletionExpired); }
            bool verified = capabilities.Verify(request.CompletionCapability, current, now);
            if (!verified) { throw new JobException(JobError.CompletionNotAuthorized); }
            if (current.ResultStateCode == (int)JobResultState.Purged) { throw new JobException(JobError.JobUnavailable); }
            if (current.ResultStateCode == (int)JobResultState.Available)
            {
                if (current.ResultHash != hash || current.ResultJson != json || current.OutcomeCode != (int)request.Outcome)
                { throw new JobException(JobError.JobConflict); }
                return Receipt(current, true);
            }
            AgentJob next = current with
            {
                ResultStateCode = (int)JobResultState.Available, OutcomeCode = (int)request.Outcome,
                ResultJson = json, ResultHash = hash, ResultReceivedUtc = now,
                LateArrival = now > current.ResultDeadlineUtc, PayloadPurgeAfterUtc = now.AddDays(options.PayloadRetentionDays)
            };
            AgentJob? saved = await TryPersistAsync(next, false, cancellationToken);
            if (saved is not null) { return Receipt(saved, false); }
        }
        throw new JobException(JobError.StorageConflict);
    }

    public async Task<AgentJob> MaintainAsync(DateTimeOffset now, int retentionDays, CancellationToken cancellationToken)
    {
        if (retentionDays is < 1 or > 365) { throw new JobException(JobError.InvalidRequest); }
        for (int attempt = 0; attempt < 4; attempt++)
        {
            AgentJob current = await ReadCurrentAsync(cancellationToken) ?? throw new JobException(JobError.JobUnavailable);
            AgentJob next = current with { };
            if (next.DispatchStateCode == (int)JobDispatchState.Sending && (next.LeaseUntilUtc is null || next.LeaseUntilUtc <= now))
            { next = next with { DispatchStateCode = (int)JobDispatchState.Held, LeaseOwner = null, LeaseUntilUtc = null,
                LastErrorCode = (int)GatewayError.UpstreamOutcomeUnknown }; }
            if (next.ResultStateCode == (int)JobResultState.Pending && now >= next.ResultDeadlineUtc)
            {
                next = next with { ResultStateCode = (int)JobResultState.DeadlineExceeded };
                if (next.DispatchStateCode == (int)JobDispatchState.Registered && next.Attempts == 0 && next.FirstAttemptUtc is null)
                { next = next with { DispatchStateCode = (int)JobDispatchState.Rejected, LastErrorCode = (int)GatewayError.DeadlineExceeded }; }
            }
            bool knownDisposition = next.DispatchStateCode is (int)JobDispatchState.Accepted or (int)JobDispatchState.Rejected;
            if (next.ResultStateCode == (int)JobResultState.DeadlineExceeded && now >= next.CapabilityExpiresUtc && knownDisposition)
            { next = next with { ResultStateCode = (int)JobResultState.ClosedWithoutResult,
                PayloadPurgeAfterUtc = next.CapabilityExpiresUtc.AddDays(retentionDays) }; }
            if (knownDisposition && next.PayloadPurgeAfterUtc <= now
                && next.ResultStateCode is (int)JobResultState.Available or (int)JobResultState.ClosedWithoutResult)
            { next = next with { ResultStateCode = (int)JobResultState.Purged, RequestJson = null,
                ResultJson = null, ProviderConversationKey = null }; }
            if (next == current) { return current; }
            AgentJob? saved = await TryPersistAsync(next, false, cancellationToken);
            if (saved is not null) { return saved; }
        }
        throw new JobException(JobError.StorageConflict);
    }

    /// <summary>One-row pages give a stable persisted continuation after each action and bound per-tick work.</summary>
    public Task<EntityBag<AgentJob>> ReadPageAsync(string workspaceShard, string? cursor, CancellationToken cancellationToken) =>
        Repository.GetAsync(workspaceShard, 1, cursor, cancellationToken: cancellationToken);

    private static JobResultReceipt Receipt(AgentJob row, bool duplicate) => new(row.JobId,
        (JobResultState)row.ResultStateCode, (JobOutcome)row.OutcomeCode!, row.ResultHash!, row.ResultReceivedUtc!.Value, duplicate);

    private async Task<AgentJob?> TryPersistAsync(AgentJob next, bool create, CancellationToken cancellationToken)
    {
        next.RequireValid();
        if (!create && (string.IsNullOrEmpty(next.ETag) || next.ETag == "*"))
        { throw new JobException(JobError.StorageConflict); }
        AgentJob intended = next with { WriteId = Guid.NewGuid().ToString("N") };
        try
        {
            // Keep an untouched intended snapshot even if an adapter mutates the submitted instance.
            if (create) { _ = await Repository.CreateAsync([intended with { }], cancellationToken: cancellationToken); }
            else { _ = await Repository.UpdateAsync([intended with { }], cancellationToken: cancellationToken); }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A write response may be lost. Reconcile only this exact complete write, never adopt another writer's tag.
        }
        AgentJob? observed = await ReadCurrentAsync(cancellationToken);
        return observed is not null && (observed with { ETag = null }) == (intended with { ETag = null }) ? observed : null;
    }
}
