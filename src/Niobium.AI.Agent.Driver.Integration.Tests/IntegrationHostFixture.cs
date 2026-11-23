using Microsoft.Extensions.Hosting;
using Niobium.AI.Agent.Driver.Server;

namespace Niobium.AI.Agent.Driver.Integration.Tests;

/// <summary>Niobium modules have process-wide registration guards; build one real host per test process.</summary>
[TestClass]
public sealed class IntegrationHostFixture
{
    private static readonly Lazy<IHost> Shared = new(() => Program.CreateHost([]));
    public static IHost Host => Shared.Value;

    [AssemblyCleanup]
    public static void Cleanup()
    {
        if (Shared.IsValueCreated) { Shared.Value.Dispose(); }
    }
}
