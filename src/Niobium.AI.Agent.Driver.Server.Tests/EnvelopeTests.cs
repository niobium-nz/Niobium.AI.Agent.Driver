using System.Text;
using System.Text.Json;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Dispatch;

namespace Niobium.AI.Agent.Driver.Server.Tests;

[TestClass]
public sealed class EnvelopeTests
{
    [TestMethod]
    public void EnvelopeIsAStringAndCanonicalizationPreservesNumbersAndArrays()
    {
        GatewayFixture fixture = new();
        DispatchAgentRequest first = fixture.Request with
        {
            Input = new() { Task = "Unicode \u03a9 and \"quotes\"", Data = JsonDocument.Parse(
                "{\"z\":[3,2,1],\"a\":9007199254740993123456789,\"b\":1.2300e+99}").RootElement.Clone() }
        };
        DispatchAgentRequest reordered = first with
        {
            Input = first.Input with { Data = JsonDocument.Parse(
                "{\"b\":1.2300e+99,\"a\":9007199254740993123456789,\"z\":[3,2,1]}").RootElement.Clone() }
        };
        PreparedDispatch one = TriggerEnvelopeWriter.Prepare(first, fixture.Caller, fixture.Registration, fixture.Options);
        PreparedDispatch two = TriggerEnvelopeWriter.Prepare(reordered, fixture.Caller, fixture.Registration, fixture.Options);
        CollectionAssert.AreEqual(one.Body.ToArray(), two.Body.ToArray());
        Assert.AreEqual(one.IdempotencyKey, two.IdempotencyKey);
        using JsonDocument outer = JsonDocument.Parse(one.Body);
        Assert.AreEqual(JsonValueKind.String, outer.RootElement.GetProperty("input").ValueKind);
        using JsonDocument envelope = JsonDocument.Parse(outer.RootElement.GetProperty("input").GetString()!);
        Assert.AreEqual("niobium.agent-handoff.v1", envelope.RootElement.GetProperty("schema").GetString());
        Assert.AreEqual(first.Input.Task, envelope.RootElement.GetProperty("input").GetProperty("task").GetString());
        Assert.AreEqual("9007199254740993123456789", envelope.RootElement.GetProperty("input").GetProperty("data").GetProperty("a").GetRawText());
        Assert.IsFalse(outer.RootElement.TryGetProperty("conversation_key", out _));
    }

    [TestMethod]
    public void InputAndFinalWrapperLimitsAreSeparateAndExact()
    {
        GatewayFixture fixture = new();
        int size = TriggerEnvelopeWriter.ValidateInput(fixture.Request, 16384).Length;
        Assert.AreEqual(size, TriggerEnvelopeWriter.ValidateInput(fixture.Request, size).Length);
        GatewayException error = Assert.ThrowsExactly<GatewayException>(() => TriggerEnvelopeWriter.ValidateInput(fixture.Request, size - 1));
        Assert.AreEqual(GatewayError.InputTooLarge, error.Code);
        fixture.Options.MaxProviderBodyBytes = 1;
        error = Assert.ThrowsExactly<GatewayException>(() => TriggerEnvelopeWriter.Prepare(fixture.Request,
            fixture.Caller, fixture.Registration, fixture.Options));
        Assert.AreEqual(GatewayError.InputTooLarge, error.Code);
    }

    [TestMethod]
    public void LengthPrefixesAndPrincipalTargetSeparationAreStable()
    {
        Assert.AreNotEqual(StableIdentity.Derive("ab", "c"), StableIdentity.Derive("a", "bc"));
        Assert.AreEqual(StableIdentity.Derive("x", "\u03a9"), StableIdentity.Derive("x", "\u03a9"));
        GatewayFixture fixture = new();
        PreparedDispatch baseline = TriggerEnvelopeWriter.Prepare(fixture.Request, fixture.Caller, fixture.Registration, fixture.Options);
        PreparedDispatch otherCaller = TriggerEnvelopeWriter.Prepare(fixture.Request,
            fixture.Caller with { PrincipalKey = "another-principal" }, fixture.Registration, fixture.Options);
        PreparedDispatch otherTarget = TriggerEnvelopeWriter.Prepare(fixture.Request, fixture.Caller,
            fixture.Registration with { ApiChannelId = "agtch_test002" }, fixture.Options);
        Assert.AreNotEqual(baseline.IdempotencyKey, otherCaller.IdempotencyKey);
        Assert.AreNotEqual(baseline.IdempotencyKey, otherTarget.IdempotencyKey);
        Assert.MatchesRegex("^[A-Za-z0-9_-]{43}$", baseline.IdempotencyKey);
    }

    [TestMethod]
    public void ConversationReuseRequiresBothPoliciesAndIsNamespaced()
    {
        GatewayFixture fixture = new();
        DispatchAgentRequest request = fixture.Request with { ConversationKey = "reuse-001" };
        Assert.ThrowsExactly<GatewayException>(() => TriggerEnvelopeWriter.Prepare(request, fixture.Caller, fixture.Registration, fixture.Options));
        fixture.Options.AllowConversationReuse = true;
        Assert.ThrowsExactly<GatewayException>(() => TriggerEnvelopeWriter.Prepare(request, fixture.Caller, fixture.Registration, fixture.Options));
        var registration = fixture.Registration with { AllowConversationReuse = true };
        using JsonDocument first = JsonDocument.Parse(TriggerEnvelopeWriter.Prepare(request, fixture.Caller, registration, fixture.Options).Body);
        using JsonDocument second = JsonDocument.Parse(TriggerEnvelopeWriter.Prepare(request,
            fixture.Caller with { PrincipalKey = "different" }, registration, fixture.Options).Body);
        Assert.AreNotEqual(first.RootElement.GetProperty("conversation_key").GetString(), second.RootElement.GetProperty("conversation_key").GetString());
        Assert.AreNotEqual("reuse-001", first.RootElement.GetProperty("conversation_key").GetString());
    }

    [TestMethod]
    [DataRow("{\"target_agent\":\"research-v1\",\"handoff_id\":\"1\",\"input\":{\"task\":\"x\"},\"token\":\"forged\"}")]
    [DataRow("{\"target_agent\":\"research-v1\",\"handoff_id\":\"1\",\"input\":{\"task\":\"x\",\"url\":\"https://example.invalid\"}}")]
    [DataRow("{\"target_agent\":\"research-v1\",\"handoff_id\":\"1\",\"input\":{\"task\":\"x\",\"data\":{\"a\":1,\"a\":2}}}")]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("{}")]
    public void UnknownDuplicateOrWrongShapeArgumentsAreRejected(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.ThrowsExactly<GatewayException>(() => TriggerEnvelopeWriter.Parse(document.RootElement));
    }

    [TestMethod]
    public void DefaultDataIsAnObjectAndInputChecksDoNotCoerceTypes()
    {
        using JsonDocument valid = JsonDocument.Parse("{\"target_agent\":\"research-v1\",\"handoff_id\":\"1\",\"input\":{\"task\":\"x\"}}");
        DispatchAgentRequest request = TriggerEnvelopeWriter.Parse(valid.RootElement);
        Assert.AreEqual(JsonValueKind.Object, request.Input.Data.ValueKind);
        foreach (string data in new[] { "[]", "null", "\"text\"", "42" })
        {
            using JsonDocument wrong = JsonDocument.Parse(data);
            Assert.ThrowsExactly<GatewayException>(() => TriggerEnvelopeWriter.ValidateInput(
                request with { Input = request.Input with { Data = wrong.RootElement } }, 16384));
        }
        Assert.ThrowsExactly<GatewayException>(() => TriggerEnvelopeWriter.ValidateInput(request with { Input = request.Input with { Task = " \n" } }, 16384));
        Assert.ThrowsExactly<GatewayException>(() => TriggerEnvelopeWriter.ValidateInput(request with { Input = null! }, 16384));
        Assert.ThrowsExactly<GatewayException>(() => TriggerEnvelopeWriter.ValidateInput(request with { TargetAgent = "../escape" }, 16384));
        Assert.ThrowsExactly<GatewayException>(() => TriggerEnvelopeWriter.ValidateInput(request with { HandoffId = "bad id" }, 16384));
        Assert.ThrowsExactly<GatewayException>(() => TriggerEnvelopeWriter.ValidateInput(request with { ConversationKey = "bad/key" }, 16384));
    }

    [TestMethod]
    public void DepthAndTotalPropertyBoundsAreEnforced()
    {
        string deep = string.Concat(Enumerable.Repeat("{\"x\":", 34)) + "0" + new string('}', 34);
        using JsonDocument document = JsonDocument.Parse(deep);
        Assert.ThrowsExactly<GatewayException>(() => TriggerEnvelopeWriter.Parse(document.RootElement));
        Dictionary<string, int> many = Enumerable.Range(0, 1025).ToDictionary(i => "p" + i, i => i);
        JsonElement oversized = JsonSerializer.SerializeToElement(many);
        Assert.ThrowsExactly<GatewayException>(() => TriggerEnvelopeWriter.Parse(oversized));
    }

    [TestMethod]
    [DataRow("research-v1", true)] [DataRow("a", true)] [DataRow("UPPER", false)]
    [DataRow("-leading", false)] [DataRow("with/slash", false)] [DataRow("", false)] [DataRow(null, false)]
    public void AliasValidationIsAsciiAndBounded(string? input, bool valid) => Assert.AreEqual(valid, IdentifierRules.IsAlias(input));

    [TestMethod]
    public void IdentifierAndOptionLimitsAreFailClosed()
    {
        Assert.IsFalse(IdentifierRules.IsAlias(new string('a', 65)));
        Assert.IsTrue(IdentifierRules.IsEventId("Ab_1.x:y-z"));
        Assert.IsFalse(IdentifierRules.IsEventId(new string('a', 129)));
        Assert.IsFalse(IdentifierRules.IsEventId("\u03a9"));
        Assert.IsFalse(IdentifierRules.IsChannel("agtch_"));
        Assert.IsFalse(IdentifierRules.IsChannel("agtch_../x"));
        Assert.IsFalse(IdentifierRules.IsChannel("https://example.invalid"));
        GatewayFixture fixture = new();
        Assert.IsTrue(fixture.Options.IsValid());
        fixture.Options.RequireCaller(fixture.Caller);
        Assert.ThrowsExactly<GatewayException>(() => fixture.Options.RequireCaller(fixture.Caller with { WorkspaceKey = "wrong" }));
        Assert.ThrowsExactly<GatewayException>(() => fixture.Options.RequireCaller(fixture.Caller with { HasDispatchScope = false }));
        fixture.Options.CredentialAliases["test-token"] = "@Microsoft.KeyVault(unresolved)";
        Assert.IsFalse(fixture.Options.IsValid());
        fixture.Options.Enabled = false;
        Assert.IsTrue(fixture.Options.IsValid());
        Assert.ThrowsExactly<GatewayException>(() => fixture.Options.RequireCaller(fixture.Caller));
        fixture.Options.MaxDispatchAttempts = 4;
        Assert.IsFalse(fixture.Options.IsValid());
    }
}
