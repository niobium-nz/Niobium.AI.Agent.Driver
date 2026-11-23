using System.Text.Json;
using Microsoft.Extensions.Options;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;

namespace Niobium.AI.Agent.Driver.Server.Operations;

/// <summary>Local operator entry point only. Not an HTTP Function or MCP tool.</summary>
public sealed class RegistryConfigurationCommand(
    IDomainRepository<AgentRegistrationDomain, AgentRegistration> registrations,
    IDomainRepository<AgentGrantDomain, AgentGrant> grants,
    IOptions<AgentGatewayOptions> settings)
{
    public async Task<RegistryWriteReceipt> ExecuteAsync(RegistryChange change, CancellationToken cancellationToken)
    {
        if (!IdentifierRules.IsAlias(settings.Value.InternalWorkspaceKey))
        {
            throw new GatewayException(GatewayError.ConfigurationUnavailable);
        }
        if (change.Registration is not null && change.Grant is null)
        {
            AgentRegistrationDomain domain = await registrations.GetAsync(change.Registration.WorkspaceKey,
                change.Registration.TargetAgent, forceLoad: true, cancellationToken: cancellationToken);
            AgentRegistration result = await domain.ConfigureAsync(change.Registration, change.ExpectedETag,
                settings.Value, cancellationToken);
            return new("registration", result.TargetAgent, result.ETag!, result.WriteId);
        }
        if (change.Grant is not null && change.Registration is null)
        {
            AgentGrantDomain domain = await grants.GetAsync(change.Grant.CallerPartition,
                change.Grant.TargetAgent, forceLoad: true, cancellationToken: cancellationToken);
            AgentGrant result = await domain.ConfigureAsync(change.Grant, change.ExpectedETag,
                settings.Value, cancellationToken);
            return new("grant", result.TargetAgent, result.ETag!, result.WriteId);
        }
        throw new GatewayException(GatewayError.InvalidRequest);
    }

    public static RegistryChange Parse(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 16384)
        { throw new GatewayException(GatewayError.InputTooLarge); }
        try
        {
            return JsonSerializer.Deserialize<RegistryChange>(json, GatewayJson.Options)
                ?? throw new GatewayException(GatewayError.InvalidRequest);
        }
        catch (JsonException) { throw new GatewayException(GatewayError.InvalidRequest); }
    }
}

public sealed record RegistryChange(string? ExpectedETag, AgentRegistration? Registration, AgentGrant? Grant);
public sealed record RegistryWriteReceipt(string Kind, string TargetAgent, string ETag, string WriteId);
