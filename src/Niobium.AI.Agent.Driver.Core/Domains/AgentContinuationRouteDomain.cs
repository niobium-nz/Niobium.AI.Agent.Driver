using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Core.Domains;

public sealed class AgentContinuationRouteDomain(Lazy<IRepository<AgentContinuationRoute>> repository,
    IEnumerable<IDomainEventHandler<IDomain<AgentContinuationRoute>>> handlers)
    : GenericDomain<AgentContinuationRoute>(repository, handlers)
{
    public async Task<AgentContinuationRoute> GetAuthorizedAsync(CallerContext caller, AgentGatewayOptions options,
        DateTimeOffset now, CancellationToken token)
    {
        CheckInitialized();
        AgentContinuationRoute? route = await Repository.RetrieveAsync(PartitionKey, RowKey, cancellationToken: token);
        if (route is null || !route.IsValid(options) || route.WorkspaceKey != PartitionKey
            || route.ContinuationRef != RowKey || !route.Enabled || route.ExpiresUtc <= now
            || route.WorkspaceKey != caller.WorkspaceKey || route.InitiatorPrincipalKey != caller.PrincipalKey)
        { throw new JobException(JobError.JobUnavailable); }
        return route;
    }

    /// <summary>Configuration only. An existing route's owner and destination cannot be rebound.</summary>
    public async Task<AgentContinuationRoute> ConfigureAsync(AgentContinuationRoute value, string? expectedETag,
        AgentGatewayOptions options, CancellationToken token)
    {
        if (!value.IsValid(options) || expectedETag is "" or "*") { throw new GatewayException(GatewayError.InvalidRequest); }
        AgentContinuationRoute? current = await Repository.RetrieveAsync(value.WorkspaceKey, value.ContinuationRef, cancellationToken: token);
        if (current?.ETag != expectedETag || (current is not null && (expectedETag is null || !current.SameBinding(value))))
        { throw new GatewayException(GatewayError.RegistryConflict); }
        AgentContinuationRoute intended = value with { ETag = expectedETag, WriteId = Guid.NewGuid().ToString("N") };
        try
        {
            if (current is null) { _ = await Repository.CreateAsync([intended with { }], cancellationToken: token); }
            else { _ = await Repository.UpdateAsync([intended with { }], cancellationToken: token); }
        }
        catch (Exception) when (!token.IsCancellationRequested) { }
        AgentContinuationRoute? observed = await Repository.RetrieveAsync(value.WorkspaceKey, value.ContinuationRef, cancellationToken: token);
        if (observed is null || string.IsNullOrEmpty(observed.ETag)
            || (observed with { ETag = null }) != (intended with { ETag = null }))
        { throw new GatewayException(GatewayError.RegistryConflict); }
        return observed;
    }
}
