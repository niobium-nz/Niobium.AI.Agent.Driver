using Microsoft.Extensions.Options;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Dispatch;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;

namespace Niobium.AI.Agent.Driver.Core.Flows;

/// <summary>Coordinates current configuration reads; domains own grant and registration decisions.</summary>
public sealed class ListAgentsFlow(
    IDomainRepository<AgentRegistrationDomain, AgentRegistration> registrations,
    IDomainRepository<AgentGrantDomain, AgentGrant> grants,
    IOptions<AgentGatewayOptions> settings,
    TimeProvider clock)
{
    public async Task<ListAgentsResult> ExecuteAsync(CallerContext caller, CancellationToken cancellationToken)
    {
        AgentGatewayOptions options = settings.Value;
        options.RequireCaller(caller);
        List<AgentDescriptor> agents = [];
        int scanned = 0;
        await foreach (AgentGrantDomain grant in grants.GetAsync(AgentGrant.PartitionFor(caller),
            forceLoad: true, cancellationToken: cancellationToken))
        {
            if (++scanned > options.MaxRegistryEntries)
            { throw new GatewayException(GatewayError.RegistryLimitExceeded); }
            bool permitted = await grant.PermitsDispatchAsync(caller, clock.GetUtcNow(), cancellationToken);
            if (!permitted || grant.RowKey is null) { continue; }
            AgentRegistrationDomain registration = await registrations.GetAsync(caller.WorkspaceKey,
                grant.RowKey, forceLoad: true, cancellationToken: cancellationToken);
            await registration.ReloadAsync(cancellationToken);
            AgentRegistration? target = await registration.GetDispatchableAsync(caller, options, cancellationToken);
            if (target is not null) { agents.Add(new(target.TargetAgent, target.DisplayName, target.Description)); }
        }
        return new(agents.OrderBy(a => a.TargetAgent, StringComparer.Ordinal).ToArray());
    }
}

public sealed class DispatchAgentFlow(
    IDomainRepository<AgentRegistrationDomain, AgentRegistration> registrations,
    IDomainRepository<AgentGrantDomain, AgentGrant> grants,
    IWorkspaceAgentClient client,
    IOptions<AgentGatewayOptions> settings,
    TimeProvider clock)
{
    public async Task<DispatchAgentResult> ExecuteAsync(DispatchAgentRequest request, CallerContext caller,
        CancellationToken cancellationToken)
    {
        AgentGatewayOptions options = settings.Value;
        options.RequireCaller(caller);
        _ = TriggerEnvelopeWriter.ValidateInput(request, options.MaxInputBytes);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.DispatchTimeoutSeconds));
        PreparedDispatch prepared;
        string credential;
        try
        {
            AgentGrantDomain grant = await grants.GetAsync(AgentGrant.PartitionFor(caller), request.TargetAgent,
                forceLoad: true, cancellationToken: deadline.Token);
            // Niobium forceLoad removes the instance cache but may return a partition-cached domain.
            // Explicit reload is required for security decisions even if a caller previously listed rows.
            await grant.ReloadAsync(deadline.Token);
            bool permitted = await grant.PermitsDispatchAsync(caller, clock.GetUtcNow(), deadline.Token);
            if (!permitted) { throw new GatewayException(GatewayError.Forbidden); }
            AgentRegistrationDomain domain = await registrations.GetAsync(caller.WorkspaceKey, request.TargetAgent,
                forceLoad: true, cancellationToken: deadline.Token);
            await domain.ReloadAsync(deadline.Token);
            AgentRegistration? registration = await domain.GetDispatchableAsync(caller, options, deadline.Token);
            if (registration is null) { throw new GatewayException(GatewayError.TargetUnavailable); }
            prepared = TriggerEnvelopeWriter.Prepare(request, caller, registration, options);
            credential = options.CredentialAliases[registration.CredentialAlias];
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch (GatewayException) { throw; }
        catch (OperationCanceledException)
        {
            return new(request.TargetAgent, request.HandoffId, DispatchState.NotDispatched,
                ErrorCode: GatewayError.DeadlineExceeded, RetryAdvice: RetryAdvice.RetrySameEventOnly);
        }
        catch (Exception)
        {
            return new(request.TargetAgent, request.HandoffId, DispatchState.NotDispatched,
                ErrorCode: GatewayError.ConfigurationUnavailable, RetryAdvice: RetryAdvice.RetrySameEventOnly);
        }
        return await client.DispatchAsync(prepared, credential, deadline.Token);
    }
}
