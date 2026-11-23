using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Dispatch;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Server.Security;
using Niobium.AI.Agent.Driver.Server.Tools;
using Niobium.Database.StorageTable;

namespace Niobium.AI.Agent.Driver.Integration.Tests;

[TestClass]
public sealed class GatewayCompositionTests
{
    [TestMethod]
    public void ActualCompositionUsesTableRepositoriesAndOneNonRedirectingTransport()
    {
        IHost host = IntegrationHostFixture.Host;
        using IServiceScope scope = host.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;
        Assert.IsInstanceOfType<CloudTableRepository<AgentRegistration>>(services.GetRequiredService<IRepository<AgentRegistration>>());
        Assert.IsInstanceOfType<CloudTableRepository<AgentGrant>>(services.GetRequiredService<IRepository<AgentGrant>>());
        services.GetRequiredService<IDomainRepository<AgentRegistrationDomain, AgentRegistration>>();
        services.GetRequiredService<IDomainRepository<AgentGrantDomain, AgentGrant>>();
        services.GetRequiredService<ICallerContextAccessor>();
        services.GetRequiredService<GatewayTools>();
        services.GetRequiredService<IWorkspaceAgentClient>();
        Assert.IsFalse(services.GetRequiredService<IOptions<AgentGatewayOptions>>().Value.Enabled);
        HttpMessageHandler handler = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(IWorkspaceAgentClient));
        while (handler is DelegatingHandler delegated)
        {
            string name = handler.GetType().FullName!;
            Assert.IsFalse(name.Contains("Retry", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(name.Contains("Resilience", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(name.Contains("Logging", StringComparison.OrdinalIgnoreCase));
            handler = delegated.InnerHandler!;
        }
        switch (handler)
        {
            case SocketsHttpHandler sockets: Assert.IsFalse(sockets.AllowAutoRedirect); break;
            case HttpClientHandler client: Assert.IsFalse(client.AllowAutoRedirect); break;
            default: Assert.Fail("Unexpected primary handler: " + handler.GetType().Name); break;
        }
    }
}
