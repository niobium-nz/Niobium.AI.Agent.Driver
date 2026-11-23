using Microsoft.Extensions.Options;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Dispatch;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Flows;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Server.Tests;

internal sealed class AgentFollowUpFixture
{
    public AgentJobFixture Inner { get; } = new();
    public JobTestRepository<AgentContinuationRoute> Routes { get; } = new();
    public FollowUpProvider Provider { get; }
    public IDomainRepository<AgentContinuationRouteDomain, AgentContinuationRoute> RouteDomains =>
        new GenericDomainRepository<AgentContinuationRouteDomain, AgentContinuationRoute>(() => new(new(() => Routes), []), new(() => Routes));
    public AgentContinuationRoute Route => new()
    {
        WorkspaceKey = Inner.Owner.WorkspaceKey, ContinuationRef = "parent-route-v1", Enabled = true,
        InitiatorPrincipalKey = Inner.Owner.PrincipalKey, ParentTargetAgent = "parent-v1", ApiChannelId = "agtch_parent",
        CredentialAlias = Inner.Registration.CredentialAlias, ProviderConversationKey = "actual-parent-api-conversation",
        ExpiresUtc = Inner.Clock.Now.AddDays(2)
    };
    public AgentJobFollowUpFlow FollowUps => new(Inner.Domains, RouteDomains, Inner.Legacy.RegistrationDomains(),
        Inner.Legacy.GrantDomains(), Provider, Options.Create(Inner.Gateway), Options.Create(Inner.Settings), Inner.Clock);
    public AgentJobFlow Flow => new(Inner.Legacy.RegistrationDomains(), Inner.Legacy.GrantDomains(), Inner.Domains,
        Provider, Inner.Capabilities, Options.Create(Inner.Gateway), Options.Create(Inner.Settings), Inner.Clock, FollowUps);
    public AgentJobMaintenanceFlow Maintenance => new(Inner.MaintenanceDomains, Inner.Domains, Flow,
        Options.Create(Inner.Gateway), Options.Create(Inner.Settings), Inner.Clock, FollowUps);
    public StartAgentJobRequest Request => Inner.Request with
    { CompletionMode = JobCompletionMode.NotifyParent, ContinuationRef = Route.ContinuationRef };

    public AgentFollowUpFixture()
    {
        Provider = new(Inner.Provider);
        Inner.Gateway.AllowConversationReuse = true;
        Inner.Gateway.EnableParentContinuation = true;
        Inner.Gateway.EnableProviderRunPolling = true;
    }

    public async Task SeedAsync()
    {
        await Inner.SeedAsync();
        await Inner.Legacy.Registrations.CreateAsync([Inner.Legacy.Registration with
        { TargetAgent = Route.ParentTargetAgent, ApiChannelId = Route.ApiChannelId, AllowConversationReuse = true }]);
        await Inner.Legacy.Grants.CreateAsync([Inner.Legacy.Grant with { TargetAgent = Route.ParentTargetAgent, CanDispatch = true }]);
        await Routes.CreateAsync([Route]);
    }

    public async Task<AgentJob> StartAsync(bool complete = false)
    {
        AgentJobStatus status = await Flow.StartAsync(Request, Inner.Owner, default);
        AgentJob row = await Inner.ReadAsync(status.JobId);
        if (complete) { await Flow.SubmitAsync(Inner.Completion(row), Inner.Writer, default); }
        return await Inner.ReadAsync(row.JobId);
    }
}

internal sealed class FollowUpProvider(JobTestProvider dispatch) : IWorkspaceAgentRunClient
{
    public string? RunId { get; set; } = "apirun_synthetic";
    public int Observations { get; private set; }
    public ProviderObservation Observation { get; set; } = new(JobProviderState.Queued);
    public Func<Task>? OnObserve { get; set; }
    public Task<DispatchAgentResult> DispatchAsync(PreparedDispatch request, string credential, CancellationToken token) =>
        dispatch.DispatchAsync(request, credential, token);
    public async Task<TrackedDispatchResult> DispatchTrackedAsync(PreparedDispatch request, string credential, CancellationToken token) =>
        new(await dispatch.DispatchAsync(request, credential, token), RunId);
    public async Task<ProviderObservation> ObserveAsync(string channel, string runId, string credential, CancellationToken token)
    {
        Observations++;
        if (OnObserve is not null) { await OnObserve(); }
        return Observation;
    }
}
