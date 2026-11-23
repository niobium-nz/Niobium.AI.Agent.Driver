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
    IOptions<AgentGatewayOptions> settings,
    IDomainRepository<AgentContinuationRouteDomain, AgentContinuationRoute>? routes = null)
{
    public async Task<RegistryWriteReceipt> ExecuteAsync(RegistryChange change, CancellationToken cancellationToken)
    {
        if (!IdentifierRules.IsAlias(settings.Value.InternalWorkspaceKey))
        { throw new GatewayException(GatewayError.ConfigurationUnavailable); }
        int count = (change.Registration is null ? 0 : 1) + (change.Grant is null ? 0 : 1) + (change.Continuation is null ? 0 : 1);
        if (count != 1) { throw new GatewayException(GatewayError.InvalidRequest); }
        if (change.Registration is not null)
        {
            AgentRegistrationDomain domain = await registrations.GetAsync(change.Registration.WorkspaceKey,
                change.Registration.TargetAgent, forceLoad: true, cancellationToken: cancellationToken);
            AgentRegistration result = await domain.ConfigureAsync(change.Registration, change.ExpectedETag, settings.Value, cancellationToken);
            return new("registration", result.TargetAgent, result.ETag!, result.WriteId);
        }
        if (change.Grant is not null)
        {
            AgentGrantDomain domain = await grants.GetAsync(change.Grant.CallerPartition,
                change.Grant.TargetAgent, forceLoad: true, cancellationToken: cancellationToken);
            AgentGrant result = await domain.ConfigureAsync(change.Grant, change.ExpectedETag, settings.Value, cancellationToken);
            return new("grant", result.TargetAgent, result.ETag!, result.WriteId);
        }
        if (routes is null) { throw new GatewayException(GatewayError.ConfigurationUnavailable); }
        AgentContinuationRoute route = change.Continuation!;
        AgentContinuationRouteDomain binding = await routes.GetAsync(route.WorkspaceKey, route.ContinuationRef, forceLoad: true, cancellationToken: cancellationToken);
        AgentContinuationRoute saved = await binding.ConfigureAsync(route, change.ExpectedETag, settings.Value, cancellationToken);
        return new("continuation", saved.ContinuationRef, saved.ETag!, saved.WriteId);
    }

    public static RegistryChange Parse(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 16384) { throw new GatewayException(GatewayError.InputTooLarge); }
        try { return JsonSerializer.Deserialize<RegistryChange>(json, GatewayJson.Options) ?? throw new GatewayException(GatewayError.InvalidRequest); }
        catch (JsonException) { throw new GatewayException(GatewayError.InvalidRequest); }
    }
}

public sealed record RegistryChange(string? ExpectedETag, AgentRegistration? Registration, AgentGrant? Grant,
    AgentContinuationRoute? Continuation = null);
public sealed record RegistryWriteReceipt(string Kind, string TargetAgent, string ETag, string WriteId);
