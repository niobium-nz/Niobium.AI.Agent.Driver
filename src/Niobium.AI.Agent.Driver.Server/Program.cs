using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using Niobium.Database.StorageTable;
using Niobium.Platform.Functions;

namespace Niobium.AI.Agent.Driver.Server;

public static class Program
{
    public static void Main(string[] args) => CreateHost(args).Run();

    internal static IHost CreateHost(string[] args)
    {
        FunctionsApplicationBuilder builder = FunctionsApplication.CreateBuilder(args);
        builder.ConfigureFunctionsWebApplication();
        builder.AddPlatform();
        builder.AddDatabase();
        builder.UsePlatform();
        return builder.Build();
    }
}
