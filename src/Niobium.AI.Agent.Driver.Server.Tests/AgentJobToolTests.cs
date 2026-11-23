using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Server.Operations;
using Niobium.AI.Agent.Driver.Server.Security;
using Niobium.AI.Agent.Driver.Server.Tools;

namespace Niobium.AI.Agent.Driver.Server.Tests;

[TestClass]
public sealed class AgentJobToolTests
{
    [TestMethod]
    public async Task NativeBoundaryProducesStructuredResultsWithoutLeakingCapabilityOrInput()
    {
        AgentJobFixture f = new(); await f.SeedAsync(); Caller caller = new(f.Owner);
        AgentJobTools tools = new(caller, f.Flow, Options.Create(f.Gateway));
        CallToolResult start = await tools.InvokeAsync(JobToolOperation.Start, Context(), Binding(f.Request), default);
        Assert.IsFalse(start.IsError == true); AssertTextMatches(start);
        string id = start.StructuredContent!["job_id"]!.GetValue<string>();
        AgentJob job = await f.ReadAsync(id);
        string startText = ((TextContentBlock)start.Content.Single()).Text;
        Assert.IsFalse(startText.Contains(f.Capabilities.Issue(job), StringComparison.Ordinal));
        Assert.IsFalse(startText.Contains(f.Request.Input.Task, StringComparison.Ordinal));
        caller.Principal = f.Writer;
        CallToolResult completed = await tools.InvokeAsync(JobToolOperation.Submit, Context(), Binding(f.Completion(job)), default);
        Assert.IsFalse(completed.IsError == true); AssertTextMatches(completed);
        Assert.IsFalse(((TextContentBlock)completed.Content.Single()).Text.Contains("completion_capability", StringComparison.Ordinal));
        caller.Principal = f.Owner;
        foreach (JobToolOperation operation in new[] { JobToolOperation.Status, JobToolOperation.Result })
        {
            CallToolResult read = await tools.InvokeAsync(operation, Context(), Binding(new GetAgentJobRequest { JobId = id }), default);
            Assert.IsFalse(read.IsError == true); AssertTextMatches(read);
            Assert.AreEqual("available", read.StructuredContent!["result_state"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public async Task DisabledToolsReturnBeforeCallerAndRepositoryAccess()
    {
        AgentJobFixture f = new(); Caller caller = new(f.Owner) { Failure = new AssertFailedException("Must not authenticate") };
        AgentJobTools tools = new(caller, f.Flow, Options.Create(f.Gateway));
        f.Gateway.EnableTrackedJobAccess = false;
        foreach (JobToolOperation operation in Enum.GetValues<JobToolOperation>())
        {
            CallToolResult result = await tools.InvokeAsync(operation, Context(), null, default);
            Assert.AreEqual("tracked_jobs_disabled", result.StructuredContent!["error_code"]!.GetValue<string>());
        }
        f.Gateway.EnableTrackedJobAccess = true; f.Gateway.EnableTrackedJobs = false;
        Assert.IsTrue((await tools.InvokeAsync(JobToolOperation.Start, Context(), Binding(f.Request), default)).IsError == true);
        f.Gateway.Enabled = false;
        Assert.IsTrue((await tools.InvokeAsync(JobToolOperation.Status, Context(), null, default)).IsError == true);
        Assert.AreEqual(0, caller.Calls); Assert.AreEqual(0, f.Jobs.Reads); Assert.AreEqual(0, f.Jobs.Writes);
    }

    [TestMethod]
    public async Task ErrorsAndCancellationAreSafeAndDoNotClaimNoDurableWorkExists()
    {
        AgentJobFixture f = new(); await f.SeedAsync(); Caller caller = new(f.Owner);
        AgentJobTools tools = new(caller, f.Flow, Options.Create(f.Gateway));
        foreach ((Exception failure, string code) in new (Exception, string)[]
        {
            (new GatewayException(GatewayError.Unauthenticated), "unauthenticated"),
            (new OperationCanceledException("private text"), "deadline_exceeded"),
            (new IOException("private text"), "configuration_unavailable")
        })
        {
            caller.Failure = failure;
            CallToolResult result = await tools.InvokeAsync(JobToolOperation.Start, Context(), Binding(f.Request), default);
            Assert.IsTrue(result.IsError == true); AssertTextMatches(result);
            Assert.AreEqual(code, result.StructuredContent!["error_code"]!.GetValue<string>());
            Assert.IsFalse(((TextContentBlock)result.Content.Single()).Text.Contains("private text", StringComparison.Ordinal));
        }
        caller.Failure = null;
        CallToolResult unknown = await tools.InvokeAsync((JobToolOperation)99, Context(), Binding(new GetAgentJobRequest { JobId = "x" }), default);
        Assert.AreEqual("invalid_request", unknown.StructuredContent!["error_code"]!.GetValue<string>());
        foreach (string? malformed in new[] { null, "{bad", "{}", Binding(new { arbitrary_url = "https://example.invalid" }) })
        { Assert.IsTrue((await tools.InvokeAsync(JobToolOperation.Start, Context(), malformed, default)).IsError == true); }
        CallToolResult unsupported = await tools.InvokeAsync(JobToolOperation.Start, Context(), Binding(f.Request with { ResultSchema = "untrusted" }), default);
        Assert.AreEqual("unsupported_result_schema", unsupported.StructuredContent!["error_code"]!.GetValue<string>());
        f.Jobs.DropWrites = true;
        CallToolResult conflict = await tools.InvokeAsync(JobToolOperation.Start, Context(), Binding(f.Request), default);
        Assert.AreEqual("storage_conflict", conflict.StructuredContent!["error_code"]!.GetValue<string>());
        Assert.AreEqual("retry_same_event_only", conflict.StructuredContent!["retry_advice"]!.GetValue<string>());
        f.Jobs.DropWrites = false;
        f.Provider.OnDispatch = _ => throw new IOException("private provider response");
        CallToolResult uncertain = await tools.InvokeAsync(JobToolOperation.Start, Context(), Binding(f.Request), default);
        Assert.AreEqual("configuration_unavailable", uncertain.StructuredContent!["error_code"]!.GetValue<string>());
        Assert.IsNull(uncertain.StructuredContent!["dispatch_state"]);
        string id = AgentJob.Identity(f.Owner, f.Request.TargetAgent, f.Request.HandoffId, f.Settings.IdentityKey);
        Assert.AreEqual(JobDispatchState.Sending, (JobDispatchState)(await f.ReadAsync(id)).DispatchStateCode);
        await tools.InvokeAsync(JobToolOperation.Start, Context(), Binding(f.Request), default);
        Assert.AreEqual(1, f.Provider.Calls.Count);
    }

    [TestMethod]
    public async Task TimerIsInertWhenDisabledAndNeverLeaksFailureDetails()
    {
        AgentJobFixture f = new(); f.Gateway.EnableJobMaintenance = false;
        MaintainAgentJobs timer = new(f.Maintenance, NullLogger<MaintainAgentJobs>.Instance);
        await timer.RunAsync(new TimerInfo(), default);
        Assert.AreEqual(0, f.Cursors.Reads);
        f.Gateway.EnableJobMaintenance = true; f.Cursors.ReadFailure = new IOException("secret storage response");
        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => timer.RunAsync(new TimerInfo(), default));
        Assert.IsNull(error.InnerException); Assert.IsFalse(error.Message.Contains("secret", StringComparison.Ordinal));
        f.Cursors.ReadFailure = null;
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        await timer.RunAsync(new TimerInfo(), canceled.Token);
    }

    private static ToolInvocationContext Context() => new() { Name = "synthetic-job-test" };
    private static string Binding<T>(T arguments) => JsonSerializer.Serialize(new { name = "synthetic-job-test", arguments }, GatewayJson.Options);
    private static void AssertTextMatches(CallToolResult result)
    {
        using JsonDocument text = JsonDocument.Parse(((TextContentBlock)result.Content.Single()).Text);
        Assert.AreEqual(JsonValueKind.Object, text.RootElement.ValueKind);
        Assert.IsTrue(JsonElement.DeepEquals(text.RootElement, JsonSerializer.SerializeToElement(result.StructuredContent)));
    }
    private sealed class Caller(CallerContext principal) : ICallerContextAccessor
    {
        public CallerContext Principal { get; set; } = principal;
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }
        public Task<CallerContext> GetAsync(ToolInvocationContext invocation, CancellationToken cancellationToken)
        {
            Calls++; cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) { throw Failure; }
            return Task.FromResult(Principal);
        }
    }
}
