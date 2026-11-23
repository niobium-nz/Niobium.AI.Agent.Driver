using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Entities;

namespace Niobium.AI.Agent.Driver.Core.Domains;

public sealed class AgentRegistrationDomain(Lazy<IRepository<AgentRegistration>> repository,
    IEnumerable<IDomainEventHandler<IDomain<AgentRegistration>>> handlers)
    : GenericDomain<AgentRegistration>(repository, handlers)
{
    public async Task<AgentRegistration?> GetDispatchableAsync(CallerContext caller,
        AgentGatewayOptions options, CancellationToken cancellationToken)
    {
        AgentRegistration? entity = await TryGetEntityAsync(cancellationToken);
        return entity is not null && entity.Enabled && entity.WorkspaceKey == caller.WorkspaceKey
            && IsValid(entity, options) ? entity : null;
    }

    public static bool IsValid(AgentRegistration value, AgentGatewayOptions options) => value.Version == 1
        && IdentifierRules.IsAlias(value.WorkspaceKey)
        && value.WorkspaceKey == options.InternalWorkspaceKey
        && IdentifierRules.IsAlias(value.TargetAgent)
        && IdentifierRules.IsChannel(value.ApiChannelId)
        && IdentifierRules.IsAlias(value.CredentialAlias)
        && options.CredentialAliases.ContainsKey(value.CredentialAlias)
        && value.DisplayName is { Length: <= 160 }
        && value.Description is { Length: <= 512 }
        && value.MaxInputBytes is >= 1 and <= 16384;

    /// <summary>Operator-only conditional write with complete WriteId read-back verification.</summary>
    public async Task<AgentRegistration> ConfigureAsync(AgentRegistration value, string? expectedETag,
        AgentGatewayOptions options, CancellationToken cancellationToken = default)
    {
        if (!IsValid(value, options) || expectedETag == "*" || expectedETag == "")
        {
            throw new GatewayException(GatewayError.InvalidRequest);
        }
        AgentRegistration? existing = await Repository.RetrieveAsync(value.WorkspaceKey, value.TargetAgent,
            cancellationToken: cancellationToken);
        if (existing?.ETag != expectedETag || (existing is not null && expectedETag is null))
        {
            throw new GatewayException(GatewayError.RegistryConflict);
        }
        if (existing is not null && (existing.Version != 1 || existing.ApiChannelId != value.ApiChannelId
            || existing.CredentialAlias != value.CredentialAlias))
        {
            throw new GatewayException(GatewayError.RegistryConflict);
        }
        AgentRegistration intended = value with { ETag = expectedETag, WriteId = Guid.NewGuid().ToString("N") };
        IEnumerable<AgentRegistration> written = await SaveAsync([intended], cancellationToken: cancellationToken);
        _ = written;
        AgentRegistration? observed = await Repository.RetrieveAsync(value.WorkspaceKey, value.TargetAgent,
            cancellationToken: cancellationToken);
        if (observed is null || string.IsNullOrEmpty(observed.ETag)
            || (observed with { ETag = null }) != (intended with { ETag = null }))
        {
            throw new GatewayException(GatewayError.RegistryConflict);
        }
        return observed;
    }
}
