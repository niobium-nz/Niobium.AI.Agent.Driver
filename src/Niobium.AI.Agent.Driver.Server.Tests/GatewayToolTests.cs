using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Server.Operations;
using Niobium.AI.Agent.Driver.Server.Security;
using Niobium.AI.Agent.Driver.Server.Tools;

namespace Niobium.AI.Agent.Driver.Server.Tests;

[TestClass]
public sealed class GatewayToolTests
{
    [TestMethod]
    public async Task ResultsAreStructuredObjectsWithMatchingJsonText()
    {
        GatewayFixture fixture = new();
        await fixture.SeedAsync();
        GatewayTools tools = Tools(fixture, new Caller(fixture.Caller));
        CallToolResult list = await tools.ListAsync(new() { Name = "list_agents" }, default);
        Assert.IsFalse(list.IsError == true);
        Assert.AreEqual("research-v1", list.StructuredContent!["agents"]![0]!["target_agent"]!.GetValue<string>());
        CallToolResult dispatch = await tools.DispatchAsync(Invocation(fixture), default);
        Assert.IsFalse(dispatch.IsError == true);
        Assert.AreEqual("accepted", dispatch.StructuredContent!["dispatch_state"]!.GetValue<string>());
        string text = ((TextContentBlock)dispatch.Content.Single()).Text;
        using JsonDocument parsed = JsonDocument.Parse(text);
        Assert.AreEqual(JsonValueKind.Object, parsed.RootElement.ValueKind);
        Assert.IsTrue(JsonElement.DeepEquals(parsed.RootElement,
            JsonSerializer.SerializeToElement(dispatch.StructuredContent)));
        Assert.IsFalse(text.Contains("ApiChannel", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task AuthenticationAndMalformedArgumentsProduceSafeMcpErrors()
    {
        GatewayFixture fixture = new();
        foreach (Exception error in new Exception[]
        {
            new GatewayException(GatewayError.Unauthenticated), new IOException("private detail"), new OperationCanceledException()
        })
        {
            GatewayTools tools = Tools(fixture, new Caller(fixture.Caller) { Failure = error });
            Assert.IsTrue((await tools.ListAsync(new() { Name = "list_agents" }, default)).IsError == true);
            CallToolResult result = await tools.DispatchAsync(Invocation(fixture), default);
            Assert.IsTrue(result.IsError == true);
            Assert.IsFalse(((TextContentBlock)result.Content.Single()).Text.Contains("private detail", StringComparison.Ordinal));
        }
        GatewayTools valid = Tools(fixture, new Caller(fixture.Caller));
        CallToolResult badList = await valid.ListAsync(new() { Name = "list_agents", Arguments = new() { ["caller_id"] = "forged" } }, default);
        Assert.IsTrue(badList.IsError == true);
        CallToolResult missing = await valid.DispatchAsync(new() { Name = "dispatch_agent" }, default);
        Assert.IsTrue(missing.IsError == true);
        CallToolResult denied = await valid.DispatchAsync(Invocation(fixture), default);
        Assert.AreEqual("not_dispatched", denied.StructuredContent!["dispatch_state"]!.GetValue<string>());
        Assert.AreEqual("forbidden", denied.StructuredContent!["error_code"]!.GetValue<string>());
        Assert.AreEqual(0, fixture.Provider.Calls.Count);
    }

    [TestMethod]
    public async Task OperatorCommandHasNoTransportAndReturnsOnlyAWriteReceipt()
    {
        GatewayFixture fixture = new();
        RegistryConfigurationCommand command = new(fixture.RegistrationDomains(), fixture.GrantDomains(), Options.Create(fixture.Options));
        RegistryWriteReceipt registration = await command.ExecuteAsync(new(null, fixture.Registration, null), default);
        Assert.AreEqual("registration", registration.Kind);
        Assert.AreEqual("research-v1", registration.TargetAgent);
        Assert.IsFalse(string.IsNullOrEmpty(registration.ETag));
        Assert.IsFalse(string.IsNullOrEmpty(registration.WriteId));
        RegistryWriteReceipt grant = await command.ExecuteAsync(new(null, null, fixture.Grant), default);
        Assert.AreEqual("grant", grant.Kind);
        await Assert.ThrowsExactlyAsync<GatewayException>(() => command.ExecuteAsync(new(null, fixture.Registration, fixture.Grant), default));
        await Assert.ThrowsExactlyAsync<GatewayException>(() => command.ExecuteAsync(new(null, null, null), default));
        fixture.Options.InternalWorkspaceKey = "";
        await Assert.ThrowsExactlyAsync<GatewayException>(() => command.ExecuteAsync(new(null, fixture.Registration, null), default));
        string json = JsonSerializer.Serialize(new RegistryChange(null, fixture.Registration, null), GatewayJson.Options);
        Assert.AreEqual(fixture.Registration, RegistryConfigurationCommand.Parse(json).Registration);
        Assert.ThrowsExactly<GatewayException>(() => RegistryConfigurationCommand.Parse("null"));
        Assert.ThrowsExactly<GatewayException>(() => RegistryConfigurationCommand.Parse("{bad"));
        Assert.ThrowsExactly<GatewayException>(() => RegistryConfigurationCommand.Parse("{\"force\":true}"));
        Assert.ThrowsExactly<GatewayException>(() => RegistryConfigurationCommand.Parse(new string('x', 16385)));
    }

    private static GatewayTools Tools(GatewayFixture fixture, ICallerContextAccessor caller) =>
        new(caller, fixture.ListFlow(), fixture.DispatchFlow(), Options.Create(fixture.Options));
    private static ToolInvocationContext Invocation(GatewayFixture fixture) => new()
    {
        Name = "dispatch_agent", Arguments = new()
        {
            ["target_agent"] = fixture.Request.TargetAgent, ["handoff_id"] = fixture.Request.HandoffId,
            ["input"] = JsonSerializer.SerializeToElement(fixture.Request.Input, GatewayJson.Options)
        }
    };
    private sealed class Caller(CallerContext caller) : ICallerContextAccessor
    {
        public Exception? Failure { get; init; }
        public Task<CallerContext> GetAsync(ToolInvocationContext invocation, CancellationToken cancellationToken)
        {
            if (Failure is not null) { throw Failure; }
            return Task.FromResult(caller);
        }
    }
}
