using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Dispatch;
using Niobium.AI.Agent.Driver.Server.Integration;
using Niobium.AI.Agent.Driver.Server.Security;
using Niobium.AI.Agent.Driver.Server.Tools;
using Niobium.Platform;

namespace Niobium.AI.Agent.Driver.Server;

public static class DependencyModule
{
    public static TBuilder AddAgentGateway<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        Niobium.Platform.StorageTable.DependencyModule.AddDatabase(builder);
        builder.Services.AddAgentGatewayCore();
        builder.Services.AddOptions<AgentGatewayOptions>().BindConfiguration(nameof(AgentGatewayOptions))
            .Validate(o => o.IsValid(), "Invalid AgentGatewayOptions.").ValidateOnStart();
        builder.Services.AddOptions<GatewayAuthenticationOptions>().BindConfiguration(nameof(GatewayAuthenticationOptions))
            .Validate<IOptions<AgentGatewayOptions>>((o, g) => !g.Value.Enabled || o.IsValid(),
                "Enabled gateway requires single-tenant Entra OAuth configuration.").ValidateOnStart();
        builder.Services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(provider =>
        {
            GatewayAuthenticationOptions options = provider.GetRequiredService<IOptions<GatewayAuthenticationOptions>>().Value;
            string tenant = options.IsValid() ? options.CanonicalTenantId : Guid.Empty.ToString("D");
            return new ConfigurationManager<OpenIdConnectConfiguration>(
                "https://login.microsoftonline.com/" + tenant + "/v2.0/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = true });
        });
        builder.Services.AddTransient<ICallerContextAccessor, McpCallerContextAdapter>();
        builder.Services.AddTransient<GatewayTools>();
        builder.Services.AddSingleton<IDispatchDelay, DispatchDelay>();
        builder.Services.AddHttpClient<IWorkspaceAgentClient, WorkspaceAgentClient>(http => http.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers()
            .ConfigureAdditionalHttpMessageHandlers((handlers, _) => handlers.Clear())
            .ConfigureAutoRedirect(false);
        return builder;
    }

    public static void ConfigureGatewayTools(this FunctionsApplicationBuilder builder)
    {
        // Keep schema configuration on the native MCP trigger, not an HTTP endpoint pretending to be MCP.
        builder.ConfigureMcpTool("dispatch_agent")
            .WithProperty("target_agent", "string", "Versioned allowlisted alias returned by list_agents.", true)
            .WithProperty("handoff_id", "string", "Stable event ID, 1-128 ASCII letters/digits or ._:-. Identical across retries.", true)
            .WithProperty("input", "object", "Object containing nonblank task (string) and optional data (object). At most 16 KiB canonical JSON, depth 32 and 1024 total properties. No other fields.", true)
            .WithProperty("conversation_key", "string", "Optional stable conversation key, only when registration permits reuse.");
    }
}
