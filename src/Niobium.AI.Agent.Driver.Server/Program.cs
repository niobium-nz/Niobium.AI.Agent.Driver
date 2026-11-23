using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Server.Operations;
using Niobium.Platform.Functions;

namespace Niobium.AI.Agent.Driver.Server;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--configure-registry")
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID")))
            { throw new InvalidOperationException("The operator command must run outside the Function App."); }
            RunRegistryCommandAsync(args[1]).GetAwaiter().GetResult();
            return;
        }
        CreateHost(args).Run();
    }

    internal static async Task RunRegistryCommandAsync(string path)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length > 16384) { throw new GatewayException(GatewayError.InvalidRequest); }
            RegistryChange change = RegistryConfigurationCommand.Parse(await File.ReadAllTextAsync(path));
            using IHost host = CreateHost([]);
            using IServiceScope scope = host.Services.CreateScope();
            RegistryConfigurationCommand command = ActivatorUtilities.CreateInstance<RegistryConfigurationCommand>(scope.ServiceProvider);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
            RegistryWriteReceipt receipt = await command.ExecuteAsync(change, deadline.Token);
            Console.WriteLine(JsonSerializer.Serialize(receipt, GatewayJson.Options));
        }
        catch (GatewayException error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new GatewayToolError(error.Code,
                RetryAdvice.OperatorActionRequired), GatewayJson.Options));
            Environment.ExitCode = 1;
        }
        catch (Exception)
        {
            // SDK exception details can include entity values. Report only a safe failure category.
            Console.Error.WriteLine("registry_operation_failed");
            Environment.ExitCode = 1;
        }
    }

    internal static IHost CreateHost(string[] args)
    {
        FunctionsApplicationBuilder builder = FunctionsApplication.CreateBuilder(args);
        builder.ConfigureFunctionsWebApplication();
        builder.AddPlatform();
        builder.AddAgentGateway();
        builder.ConfigureGatewayTools();
        builder.UsePlatform();
        return builder.Build();
    }
}
