using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Entities;

namespace Niobium.AI.Agent.Driver.Core.Domains;

public sealed class AgentGrantDomain(Lazy<IRepository<AgentGrant>> repository,
    IEnumerable<IDomainEventHandler<IDomain<AgentGrant>>> handlers)
    : GenericDomain<AgentGrant>(repository, handlers)
{
    public async Task<bool> PermitsDispatchAsync(CallerContext caller, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        CheckInitialized();
        // Never revive a deleted grant from a previously initialized domain snapshot.
        AgentGrant? grant = await Repository.RetrieveAsync(PartitionKey, RowKey,
            cancellationToken: cancellationToken);
        return grant?.TargetAgent == RowKey && IsPermitted(grant, caller, now);
    }

    public static bool IsPermitted(AgentGrant? grant, CallerContext caller, DateTimeOffset now) =>
        grant is not null && grant.Version == 1 && grant.Enabled && grant.CanDispatch
        && grant.CallerPartition == AgentGrant.PartitionFor(caller)
        && IdentifierRules.IsAlias(grant.TargetAgent)
        && (grant.ExpiresUtc is null || grant.ExpiresUtc > now);

    public async Task<AgentGrant> ConfigureAsync(AgentGrant value, string? expectedETag,
        AgentGatewayOptions options, CancellationToken cancellationToken = default)
    {
        string prefix = options.InternalWorkspaceKey + ".";
        string suffix = value.CallerPartition.StartsWith(prefix, StringComparison.Ordinal)
            ? value.CallerPartition[prefix.Length..] : "";
        if (value.Version != 1 || !IdentifierRules.IsAlias(options.InternalWorkspaceKey)
            || suffix.Length != 43 || !suffix.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            || !IdentifierRules.IsAlias(value.TargetAgent) || expectedETag == "*" || expectedETag == "")
        { throw new GatewayException(GatewayError.InvalidRequest); }
        AgentGrant? existing = await Repository.RetrieveAsync(value.CallerPartition, value.TargetAgent,
            cancellationToken: cancellationToken);
        if (existing?.ETag != expectedETag || (existing is not null && (expectedETag is null || existing.Version != 1)))
        { throw new GatewayException(GatewayError.RegistryConflict); }
        AgentGrant intended = value with { ETag = expectedETag, WriteId = Guid.NewGuid().ToString("N") };
        _ = await SaveAsync([intended], cancellationToken: cancellationToken);
        AgentGrant? observed = await Repository.RetrieveAsync(value.CallerPartition, value.TargetAgent,
            cancellationToken: cancellationToken);
        if (observed is null || string.IsNullOrEmpty(observed.ETag)
            || (observed with { ETag = null }) != (intended with { ETag = null }))
        { throw new GatewayException(GatewayError.RegistryConflict); }
        return observed;
    }
}
