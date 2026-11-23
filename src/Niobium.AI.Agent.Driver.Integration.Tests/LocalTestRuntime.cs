using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Niobium.AI.Agent.Driver.Integration.Tests;

/// <summary>Test-only process ownership. Never adopts an existing listener or uses a cloud endpoint.</summary>
internal sealed class LocalTestRuntime : IAsyncDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "niobium-gateway-" + Guid.NewGuid().ToString("N"));
    private LocalTestProcess? emulator;
    public string Root { get; } = FindRoot();
    public string LogDirectory { get; }

    private LocalTestRuntime()
    {
        LogDirectory = Path.Combine(Root, "artifacts", "compatibility", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        Directory.CreateDirectory(LogDirectory);
    }

    public static async Task<LocalTestRuntime> StartAsync()
    {
        LocalTestRuntime runtime = new();
        try
        {
            RequireFreePorts(10000, 10001, 10002);
            runtime.emulator = runtime.StartNpm("azurite@3.35.0", "azurite", "azurite",
                "--silent", "--skipApiVersionCheck", "--location", Path.Combine(runtime.temporary, "storage"),
                "--blobHost", "127.0.0.1", "--queueHost", "127.0.0.1", "--tableHost", "127.0.0.1");
            await runtime.emulator.WaitForHttpAsync(new Uri("http://127.0.0.1:10002/devstoreaccount1"), false);
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync();
            throw;
        }
    }

    public async Task VerifyNativeMcpAsync()
    {
        RequireFreePorts(7071);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string builtHost = Path.Combine(Root, "src", "Niobium.AI.Agent.Driver.Server", "bin", configuration, "net10.0");
        string snapshot = Path.Combine(temporary, "host");
        if (!File.Exists(Path.Combine(builtHost, "functions.metadata")))
        { throw new InvalidOperationException("Build the solution before running native MCP integration tests."); }
        foreach (string source in Directory.EnumerateFiles(builtHost, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(source).Equals("local.settings.json", StringComparison.OrdinalIgnoreCase)) { continue; }
            string target = Path.Combine(snapshot, Path.GetRelativePath(builtHost, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, true);
        }
        // Copy built output instead of rebuilding assemblies currently instrumented by the test runner.
        await using LocalTestProcess host = StartNpm("azure-functions-core-tools@4.17.0", "func", "functions-host",
            "start", "--script-root", snapshot, "--no-build", "--port", "7071");
        await host.WaitForHttpAsync(new Uri("http://127.0.0.1:7071/health"), true);
        await using LocalTestProcess probe = Start("node", "mcp-wire", Path.Combine(Root, "scripts", "test-mcp-wire.mjs"));
        await probe.RequireSuccessAsync(TimeSpan.FromSeconds(90));
        await using LocalTestProcess version = StartNpm("azure-functions-core-tools@4.17.0", "func", "core-tools-version", "--version");
        await version.RequireSuccessAsync(TimeSpan.FromSeconds(60));
    }

    public async Task CompileAuthenticationTemplateAsync()
    {
        await using LocalTestProcess compiler = Start("az", "gateway-auth-bicep", "bicep", "build", "--file",
            Path.Combine(Root, "infra", "gateway-auth.bicep"), "--outfile", Path.Combine(temporary, "gateway-auth.json"));
        await compiler.RequireSuccessAsync(TimeSpan.FromMinutes(3));
    }

    private LocalTestProcess StartNpm(string package, string command, string logName, params string[] arguments)
    {
        // The repository's existing local verification entrypoints require a POSIX environment.
        // Use WSL on Windows rather than introducing a shell-interpolated command line.
        if (OperatingSystem.IsWindows())
        { throw new PlatformNotSupportedException("Run local service integration tests in WSL, Linux or macOS."); }
        return Start("npm", logName, ["exec", "--yes", "--package=" + package, "--", command, .. arguments]);
    }

    private LocalTestProcess Start(string executable, string logName, params string[] arguments) =>
        new(executable, arguments, Root, Path.Combine(LogDirectory, logName), temporary);

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Niobium.AI.Agent.Driver.slnx"))) { return directory.FullName; }
        }
        throw new InvalidOperationException("Run integration tests inside a Driver checkout.");
    }

    private static void RequireFreePorts(params int[] ports)
    {
        foreach (int port in ports)
        {
            TcpListener listener = new(IPAddress.Loopback, port);
            try { listener.Start(); }
            catch (SocketException error)
            { throw new InvalidOperationException($"Local integration port {port} is occupied; no existing process will be used or stopped.", error); }
            finally { listener.Stop(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (emulator is not null) { await emulator.DisposeAsync(); emulator = null; }
        if (Directory.Exists(temporary)) { Directory.Delete(temporary, true); }
    }
}

/// <summary>Captures logs, bounds waits and stops only the process tree started by this test.</summary>
internal sealed class LocalTestProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> output;
    private readonly Task<string> error;
    private readonly string logPrefix;
    private bool disposed;

    public LocalTestProcess(string executable, string[] arguments, string directory, string logPrefix, string temporary)
    {
        this.logPrefix = logPrefix;
        ProcessStartInfo info = new(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) { info.ArgumentList.Add(argument); }
        // Do not pass deployment credentials, telemetry connection strings or real gateway settings to local hosts.
        info.Environment.Clear();
        foreach (string name in new[] { "PATH", "HOME", "USERPROFILE", "SystemRoot", "DOTNET_ROOT", "DOTNET_ROOT_X64", "TMPDIR", "TMP", "TEMP", "LANG" })
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (value is not null) { info.Environment[name] = value; }
        }
        info.Environment["FUNCTIONS_CORE_TOOLS_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["AZURE_CONFIG_DIR"] = Path.Combine(temporary, "azure-cli");
        info.Environment["FUNCTIONS_WORKER_RUNTIME"] = "dotnet-isolated";
        info.Environment["AZURE_FUNCTIONS_ENVIRONMENT"] = "Development";
        info.Environment["AzureWebJobsStorage"] = "UseDevelopmentStorage=true";
        info.Environment["AgentGatewayOptions__Enabled"] = "false";
        process = Process.Start(info) ?? throw new InvalidOperationException("Could not start local test dependency: " + executable);
        output = process.StandardOutput.ReadToEndAsync();
        error = process.StandardError.ReadToEndAsync();
    }

    public async Task WaitForHttpAsync(Uri uri, bool requireSuccess)
    {
        if (!uri.IsLoopback || uri.Scheme != "http") { throw new InvalidOperationException("Test probes must use loopback HTTP."); }
        using HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(1) };
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromMinutes(3))
        {
            if (process.HasExited)
            {
                await SaveLogsAsync();
                throw new InvalidOperationException("Local dependency exited before readiness. See " + logPrefix);
            }
            try
            {
                using HttpResponseMessage response = await http.GetAsync(uri);
                if (!requireSuccess || response.IsSuccessStatusCode) { return; }
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) { }
            await Task.Delay(250);
        }
        throw new TimeoutException("Local dependency did not become ready. See " + logPrefix);
    }

    public async Task RequireSuccessAsync(TimeSpan timeout)
    {
        using CancellationTokenSource deadline = new(timeout);
        await process.WaitForExitAsync(deadline.Token);
        await SaveLogsAsync();
        if (process.ExitCode != 0)
        { throw new InvalidOperationException($"Local verification exited with {process.ExitCode}. See {logPrefix}.stderr.log and .stdout.log."); }
    }

    private async Task SaveLogsAsync()
    {
        await File.WriteAllTextAsync(logPrefix + ".stdout.log", await output.WaitAsync(TimeSpan.FromSeconds(15)));
        await File.WriteAllTextAsync(logPrefix + ".stderr.log", await error.WaitAsync(TimeSpan.FromSeconds(15)));
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) { return; }
        disposed = true;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            await SaveLogsAsync();
        }
        finally { process.Dispose(); }
    }
}
