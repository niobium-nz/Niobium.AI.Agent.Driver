using Microsoft.Extensions.Options;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Dispatch;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Core.Flows;

/// <summary>Optional read-only observation and allowlisted parent notification, each separately gated.</summary>
public sealed class AgentJobFollowUpFlow(
    IDomainRepository<AgentJobDomain, AgentJob> jobs,
    IDomainRepository<AgentContinuationRouteDomain, AgentContinuationRoute> routes,
    IDomainRepository<AgentRegistrationDomain, AgentRegistration> registrations,
    IDomainRepository<AgentGrantDomain, AgentGrant> grants,
    IWorkspaceAgentClient client, IOptions<AgentGatewayOptions> settings,
    IOptions<TrackedJobOptions> jobSettings, TimeProvider clock)
{
    public async Task<AgentJob> BindAsync(AgentJob job, StartAgentJobRequest request, CallerContext caller, CancellationToken token)
    {
        settings.Value.RequireCaller(caller);
        if (!settings.Value.EnableParentContinuation || !settings.Value.EnableTrackedJobAccess
            || request.CompletionMode != JobCompletionMode.NotifyParent || !IdentifierRules.IsAlias(request.ContinuationRef)
            || job.WorkspaceKey != caller.WorkspaceKey || job.InitiatorPrincipalKey != caller.PrincipalKey)
        { throw new JobException(JobError.JobUnavailable); }
        AgentContinuationRouteDomain domain = await routes.GetAsync(caller.WorkspaceKey, request.ContinuationRef!, forceLoad: true, cancellationToken: token);
        AgentContinuationRoute route = await domain.GetAuthorizedAsync(caller, settings.Value, clock.GetUtcNow(), token);
        AgentJob bound = route.Bind(job);
        await AuthorizeParentAsync(bound, caller, token);
        return bound;
    }

    public async Task<AgentJob> ExecuteAsync(AgentJob snapshot, CancellationToken token)
    {
        AgentGatewayOptions options = settings.Value;
        if (!options.Enabled || !options.EnableTrackedJobAccess || (!options.EnableProviderRunPolling && !options.EnableParentContinuation))
        { return snapshot; }
        CallerContext owner = new(snapshot.WorkspaceKey, snapshot.InitiatorPrincipalKey, true);
        options.RequireCaller(owner);
        AgentJobDomain domain = await jobs.GetAsync(AgentJob.PartitionFor(owner.WorkspaceKey, snapshot.JobId),
            snapshot.JobId, forceLoad: true, cancellationToken: token);
        AgentJob current = await domain.GetForReaderAsync(owner, token);
        // Process the durable outbox first so a denied/unavailable observation cannot starve parent delivery.
        if (options.EnableParentContinuation && current.CanNotify(clock.GetUtcNow()))
        {
            bool authorized = true;
            try { await AuthorizeParentAsync(current, owner, token); }
            catch (JobException) { authorized = false; }
            catch (GatewayException) { authorized = false; }
            if (!authorized) { await domain.HoldPendingContinuationAsync(token); }
            else
            {
                PreparedDispatch prepared = current.PrepareContinuation(options.MaxProviderBodyBytes);
                AgentJob? claim = await domain.ClaimContinuationAsync(Guid.NewGuid().ToString("N"), clock.GetUtcNow(),
                    jobSettings.Value.DispatchLeaseSeconds, token);
                if (claim is not null)
                {
                    DispatchAgentResult outcome = await client.DispatchAsync(prepared,
                        options.CredentialAliases[claim.ParentCredentialAlias!], token);
                    await domain.RecordContinuationAsync(claim, outcome, clock.GetUtcNow(), token);
                }
            }
            current = await domain.GetForReaderAsync(owner, token);
        }
        if (options.EnableProviderRunPolling && current.CanObserve(clock.GetUtcNow()))
        {
            if (client is not IWorkspaceAgentRunClient observer) { throw new JobException(JobError.ConfigurationUnavailable); }
            await AuthorizeObservationAsync(current, owner, token);
            AgentJob? claim = await domain.ClaimObservationAsync(Guid.NewGuid().ToString("N"), clock.GetUtcNow(), token);
            if (claim is not null)
            {
                ProviderObservation observation = await observer.ObserveAsync(claim.ApiChannelId, claim.ProviderRunId!,
                    options.CredentialAliases[claim.CredentialAlias], token);
                await domain.RecordObservationAsync(claim, observation, clock.GetUtcNow(), token);
                current = await domain.GetForReaderAsync(owner, token);
            }
        }
        return current;
    }

    private async Task AuthorizeParentAsync(AgentJob job, CallerContext owner, CancellationToken token)
    {
        if (!settings.Value.AllowConversationReuse || job.ContinuationExpiresUtc <= clock.GetUtcNow())
        { throw new JobException(JobError.JobUnavailable); }
        AgentContinuationRouteDomain routeDomain = await routes.GetAsync(owner.WorkspaceKey, job.ContinuationRef!, forceLoad: true, cancellationToken: token);
        AgentContinuationRoute route = await routeDomain.GetAuthorizedAsync(owner, settings.Value, clock.GetUtcNow(), token);
        if (!route.Matches(job)) { throw new JobException(JobError.JobUnavailable); }
        AgentGrantDomain grant = await grants.GetAsync(AgentGrant.PartitionFor(owner), job.ParentTargetAgent!, forceLoad: true, cancellationToken: token);
        bool allowed = await grant.PermitsDispatchAsync(owner, clock.GetUtcNow(), token);
        if (!allowed) { throw new GatewayException(GatewayError.Forbidden); }
        AgentRegistrationDomain registration = await registrations.GetAsync(owner.WorkspaceKey, job.ParentTargetAgent!, forceLoad: true, cancellationToken: token);
        AgentRegistration? parent = await registration.GetDispatchableAsync(owner, settings.Value, token);
        if (parent is null || !parent.AllowConversationReuse || parent.ApiChannelId != job.ParentApiChannelId
            || parent.CredentialAlias != job.ParentCredentialAlias)
        { throw new JobException(JobError.JobUnavailable); }
    }

    private async Task AuthorizeObservationAsync(AgentJob job, CallerContext owner, CancellationToken token)
    {
        AgentGrantDomain grant = await grants.GetAsync(AgentGrant.PartitionFor(owner), job.TargetAgent, forceLoad: true, cancellationToken: token);
        bool allowed = await grant.PermitsJobAsync(owner, false, clock.GetUtcNow(), token);
        if (!allowed) { throw new GatewayException(GatewayError.Forbidden); }
        AgentRegistrationDomain registration = await registrations.GetAsync(owner.WorkspaceKey, job.TargetAgent, forceLoad: true, cancellationToken: token);
        AgentRegistration? target = await registration.GetDispatchableAsync(owner, settings.Value, token);
        if (target is null || target.Version != 2 || target.ApiChannelId != job.ApiChannelId || target.CredentialAlias != job.CredentialAlias)
        { throw new JobException(JobError.JobUnavailable); }
    }
}
