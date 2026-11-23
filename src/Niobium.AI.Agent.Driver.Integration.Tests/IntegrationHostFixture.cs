using Microsoft.Extensions.Hosting;
using Niobium.AI.Agent.Driver.Server;

[assembly: DoNotParallelize]

namespace Niobium.AI.Agent.Driver.Integration.Tests;

/// <summary>One application host and one owned emulator per test process, including azd packaging.</summary>
[TestClass]
public sealed class IntegrationHostFixture
{
    private static readonly Lazy<IHost> Shared = new(() => Program.CreateHost([]));
    public static IHost Host => Shared.Value;
    internal static LocalTestRuntime Runtime { get; private set; } = null!;

    [AssemblyInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        Runtime = await LocalTestRuntime.StartAsync();
        context.WriteLine("Pinned local test services started. Logs: " + Runtime.LogDirectory);
    }

    [AssemblyCleanup]
    public static async Task CleanupAsync()
    {
        try
        {
            if (Shared.IsValueCreated) { Shared.Value.Dispose(); }
        }
        finally
        {
            if (Runtime is not null) { await Runtime.DisposeAsync(); }
        }
    }
}
