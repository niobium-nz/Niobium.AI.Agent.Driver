using Microsoft.Extensions.Options;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Dispatch;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Core.Flows;

/// <summary>Coordinates opt-in tracked jobs. Domains own authorization and conditional state transitions.</summary>
public sealed class AgentJobFlow(
    IDomainRepository<AgentRegistrationDomain, AgentRegistration> registrations,
    IDomainRepository<AgentGrantDomain, AgentGrant> grants,
    IDomainRepository<AgentJobDomain, AgentJob> jobs,
    IWorkspaceAgentClient client, IJobCapabilities capabilities,
    IOptions<AgentGatewayOptions> gateway, IOptions<TrackedJobOptions> settings, TimeProvider clock,
    AgentJobFollowUpFlow? followUps = null)
{
    public async Task<AgentJobStatus> StartAsync(StartAgentJobRequest request, CallerContext caller, CancellationToken cancellationToken)
    {
        RequireAccess(caller, true);
        AgentGatewayOptions options = gateway.Value;
        TrackedJobOptions jobOptions = settings.Value;
        string json = JobWire.CanonicalRequest(request, options);
        AgentRegistration target = await AuthorizeAsync(caller, request.TargetAgent, false, cancellationToken);
        _ = TriggerEnvelopeWriter.ValidateInput(new()
        { TargetAgent = request.TargetAgent, HandoffId = request.HandoffId, Input = request.Input, ConversationKey = request.ConversationKey },
            Math.Min(options.MaxInputBytes, target.MaxInputBytes));
        if (request.ConversationKey is not null && (!options.AllowConversationReuse || !target.AllowConversationReuse))
        { throw new GatewayException(GatewayError.Forbidden); }
        string id = AgentJob.Identity(caller, request.TargetAgent, request.HandoffId, jobOptions.IdentityKey);
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds());
        AgentJob intended = new()
        {
            Version = options.EnableProviderRunPolling ? 2 : 1, ObserveProviderRun = options.EnableProviderRunPolling,
            WorkspaceShard = AgentJob.PartitionFor(caller.WorkspaceKey, id), JobId = id,
            WorkspaceKey = caller.WorkspaceKey, InitiatorPrincipalKey = caller.PrincipalKey,
            TargetAgent = target.TargetAgent, HandoffId = request.HandoffId,
            RequestJson = json, RequestFingerprint = JobWire.Hash(json), ApiChannelId = target.ApiChannelId,
            CredentialAlias = target.CredentialAlias, ResultSchema = target.ResultSchema,
            ResultWriterPrincipalKey = target.ResultWriterPrincipalKey,
            SigningKeyVersion = jobOptions.ActiveSigningKeyVersion, CapabilityNonce = Guid.NewGuid().ToString("N"),
            CreatedUtc = now, ResultDeadlineUtc = now.AddMinutes(jobOptions.ResultDeadlineMinutes),
            CapabilityExpiresUtc = now.AddMinutes(jobOptions.ResultDeadlineMinutes + jobOptions.CallbackGraceMinutes),
            ProviderConversationKey = request.ConversationKey is null ? null : StableIdentity.Derive(
                "niobium-job-conversation-v1", caller.WorkspaceKey, caller.PrincipalKey, target.TargetAgent, request.ConversationKey)
        };
        if (request.CompletionMode == JobCompletionMode.NotifyParent)
        {
            if (followUps is null) { throw new JobException(JobError.ConfigurationUnavailable); }
            intended = await followUps.BindAsync(intended, request, caller, cancellationToken);
        }
        intended.RequireValid();
        _ = JobWire.Prepare(intended, capabilities, options.MaxProviderBodyBytes);
        AgentJobDomain domain = await DomainAsync(caller.WorkspaceKey, id, cancellationToken);
        AgentJob registered = await domain.RegisterAsync(intended, cancellationToken);
        AgentJob observed = await DispatchRegisteredAsync(registered, cancellationToken);
        return observed.Status();
    }

    public async Task<JobResultReceipt> SubmitAsync(SubmitJobResultRequest request, CallerContext caller, CancellationToken cancellationToken)
    {
        RequireAccess(caller, false);
        AgentJobDomain domain = await DomainAsync(caller.WorkspaceKey, request.JobId, cancellationToken);
        AgentJob job = await domain.ReadCurrentAsync(cancellationToken) ?? throw new JobException(JobError.JobUnavailable);
        if (job.WorkspaceKey != caller.WorkspaceKey || job.ResultWriterPrincipalKey != caller.PrincipalKey)
        { throw new JobException(JobError.CompletionNotAuthorized); }
        AgentRegistration target = await AuthorizeAsync(caller, job.TargetAgent, true, cancellationToken);
        RequireBinding(job, target);
        return await domain.SubmitAsync(caller, request, capabilities, settings.Value, clock.GetUtcNow(), cancellationToken);
    }

    public async Task<AgentJobStatus> StatusAsync(GetAgentJobRequest request, CallerContext caller, CancellationToken cancellationToken)
    {
        RequireAccess(caller, false);
        AgentJobDomain domain = await DomainAsync(caller.WorkspaceKey, request.JobId, cancellationToken);
        AgentJob job = await domain.GetForReaderAsync(caller, cancellationToken);
        return job.Status();
    }

    public async Task<AgentJobResult> ResultAsync(GetAgentJobRequest request, CallerContext caller, CancellationToken cancellationToken)
    {
        RequireAccess(caller, false);
        AgentJobDomain domain = await DomainAsync(caller.WorkspaceKey, request.JobId, cancellationToken);
        AgentJob job = await domain.GetForReaderAsync(caller, cancellationToken);
        return job.Result();
    }

    /// <summary>Only a confirmed, never-attempted registration can acquire a send claim.</summary>
    public async Task<AgentJob> DispatchRegisteredAsync(AgentJob snapshot, CancellationToken cancellationToken)
    {
        CallerContext owner = new(snapshot.WorkspaceKey, snapshot.InitiatorPrincipalKey, true);
        RequireAccess(owner, true);
        AgentJobDomain domain = await DomainAsync(snapshot.WorkspaceKey, snapshot.JobId, cancellationToken);
        AgentJob current = await domain.ReadCurrentAsync(cancellationToken) ?? throw new JobException(JobError.JobUnavailable);
        if (current.DispatchStateCode != (int)JobDispatchState.Registered || current.ResultStateCode != (int)JobResultState.Pending
            || current.FirstAttemptUtc is not null || current.Attempts != 0 || clock.GetUtcNow() >= current.ResultDeadlineUtc)
        { return current; }
        AgentRegistration target = await AuthorizeAsync(owner, current.TargetAgent, false, cancellationToken);
        RequireBinding(current, target);
        PreparedDispatch prepared = JobWire.Prepare(current, capabilities, gateway.Value.MaxProviderBodyBytes);
        string credential = gateway.Value.CredentialAliases[current.CredentialAlias];
        bool observe = current.ObserveProviderRun && gateway.Value.EnableProviderRunPolling;
        if (observe && client is not IWorkspaceAgentRunClient) { throw new JobException(JobError.ConfigurationUnavailable); }
        AgentJob? claim = await domain.ClaimDispatchAsync(Guid.NewGuid().ToString("N"), clock.GetUtcNow(),
            settings.Value.DispatchLeaseSeconds, cancellationToken);
        if (claim is null) { return await domain.ReadCurrentAsync(cancellationToken) ?? throw new JobException(JobError.JobUnavailable); }
        TrackedDispatchResult result = observe
            ? await ((IWorkspaceAgentRunClient)client).DispatchTrackedAsync(prepared, credential, cancellationToken)
            : new(await client.DispatchAsync(prepared, credential, cancellationToken));
        return await domain.RecordDispatchAsync(claim, result.Outcome, clock.GetUtcNow(), cancellationToken, result.RunId);
    }

    private void RequireAccess(CallerContext caller, bool start)
    {
        gateway.Value.RequireCaller(caller);
        if (!gateway.Value.EnableTrackedJobAccess || (start && !gateway.Value.EnableTrackedJobs))
        { throw new JobException(JobError.TrackedJobsDisabled); }
        if (!settings.Value.IsValid()) { throw new JobException(JobError.ConfigurationUnavailable); }
    }

    private Task<AgentJobDomain> DomainAsync(string workspace, string id, CancellationToken token) =>
        jobs.GetAsync(AgentJob.PartitionFor(workspace, id), id, forceLoad: true, cancellationToken: token);

    private async Task<AgentRegistration> AuthorizeAsync(CallerContext caller, string alias, bool completion, CancellationToken token)
    {
        AgentGrantDomain grant = await grants.GetAsync(AgentGrant.PartitionFor(caller), alias, forceLoad: true, cancellationToken: token);
        bool allowed = await grant.PermitsJobAsync(caller, completion, clock.GetUtcNow(), token);
        if (!allowed) { throw new GatewayException(GatewayError.Forbidden); }
        AgentRegistrationDomain registration = await registrations.GetAsync(caller.WorkspaceKey, alias, forceLoad: true, cancellationToken: token);
        AgentRegistration? target = await registration.GetDispatchableAsync(caller, gateway.Value, token);
        if (target is null || target.Version != 2 || target.ResultSchema != JobWire.ResultSchemaId)
        { throw new JobException(JobError.JobUnavailable); }
        if (completion && target.ResultWriterPrincipalKey != caller.PrincipalKey)
        { throw new JobException(JobError.CompletionNotAuthorized); }
        return target;
    }

    private static void RequireBinding(AgentJob job, AgentRegistration target)
    {
        if (job.ApiChannelId != target.ApiChannelId || job.CredentialAlias != target.CredentialAlias
            || job.ResultSchema != target.ResultSchema || job.ResultWriterPrincipalKey != target.ResultWriterPrincipalKey)
        { throw new JobException(JobError.JobUnavailable); }
    }
}
