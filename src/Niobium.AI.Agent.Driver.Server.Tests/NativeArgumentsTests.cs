using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Dispatch;
using Niobium.AI.Agent.Driver.Server.Tools;

namespace Niobium.AI.Agent.Driver.Server.Tests;

[TestClass]
public sealed class NativeArgumentsTests
{
    [TestMethod]
    public void NativeBindingPreservesLargeNumbersCaseDistinctKeysAndDateStrings()
    {
        const string payload = "{\"name\":\"dispatch_agent\",\"arguments\":{\"target_agent\":\"research-v1\",\"handoff_id\":\"test-1\",\"input\":{\"task\":\"test\",\"data\":{\"value\":1.2345678901234567890123456789012345e+99,\"Date\":\"2026-11-23T00:00:00Z\",\"date\":\"different\"}}}}";
        var arguments = NativeGatewayFunctions.ReadRawArguments(payload);
        var request = TriggerEnvelopeWriter.Parse(arguments);
        Assert.AreEqual("1.2345678901234567890123456789012345e+99", request.Input.Data.GetProperty("value").GetRawText());
        Assert.AreEqual("2026-11-23T00:00:00Z", request.Input.Data.GetProperty("Date").GetString());
        Assert.AreEqual("different", request.Input.Data.GetProperty("date").GetString());
    }
    [TestMethod]
    public void MissingMalformedAndUnboundedBindingPayloadsFailClosed()
    {
        foreach (string? payload in new[] { null, "{bad", "[]", "{}", new string('x', 131073) })
        { Assert.ThrowsExactly<GatewayException>(() => NativeGatewayFunctions.ReadRawArguments(payload)); }
    }
}
