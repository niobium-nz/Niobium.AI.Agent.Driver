using Microsoft.Extensions.DependencyInjection;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Flows;

namespace Niobium.AI.Agent.Driver.Core;

public static class DependencyModule
{
    public static IServiceCollection AddAgentGatewayCore(this IServiceCollection services)
    {
        services.AddDomain<AgentRegistrationDomain, AgentRegistration>();
        services.AddDomain<AgentGrantDomain, AgentGrant>();
        services.AddTransient<ListAgentsFlow>();
        services.AddTransient<DispatchAgentFlow>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
