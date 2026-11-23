using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Server.Tests;

[TestClass]
public sealed class AgentJobSecurityTests
{
    [TestMethod]
    public void CapabilityBytesAreStableAndEveryJobBindingIsEnforced()
    {
        AgentJobFixture f = new(); AgentJob job = f.Intent();
        string capability = f.Capabilities.Issue(job);
        Assert.AreEqual(capability, f.Capabilities.Issue(job with { ETag = "different", WriteId = "different", DispatchStateCode = 2 }));
        Assert.IsTrue(f.Capabilities.Verify(capability, job, f.Clock.Now));
        JwtSecurityToken token = new JwtSecurityTokenHandler().ReadJwtToken(capability);
        Assert.AreEqual(SecurityAlgorithms.HmacSha256, token.Header.Alg);
        Assert.AreEqual(job.SigningKeyVersion, token.Header.Kid);
        Assert.AreEqual(job.CapabilityExpiresUtc.ToUnixTimeSeconds(), token.Payload.Expiration);
        foreach (AgentJob changed in new[]
        {
            job with { JobId = f.Intent("other").JobId }, job with { WorkspaceKey = "other" },
            job with { TargetAgent = "other-v1" }, job with { ResultWriterPrincipalKey = f.Owner.PrincipalKey },
            job with { CapabilityNonce = Guid.NewGuid().ToString("N") }, job with { CreatedUtc = job.CreatedUtc.AddSeconds(-1) },
            job with { CapabilityExpiresUtc = job.CapabilityExpiresUtc.AddSeconds(1) }, job with { SigningKeyVersion = "unknown-key" }
        }) { Assert.IsFalse(f.Capabilities.Verify(capability, changed, f.Clock.Now)); }
        Assert.IsFalse(f.Capabilities.Verify(capability, job, job.CreatedUtc.AddSeconds(-1)));
        Assert.IsFalse(f.Capabilities.Verify(capability, job, job.CapabilityExpiresUtc));
        foreach (string invalid in new[] { "", "malformed", new string('a', 4097), capability[..^4] + "AAAA" })
        { Assert.IsFalse(f.Capabilities.Verify(invalid, job, f.Clock.Now)); }
    }

    [TestMethod]
    public void UnsignedWrongAlgorithmAndAlteredClaimsFailEvenWhenKeyIsKnown()
    {
        AgentJobFixture f = new(); AgentJob job = f.Intent();
        JwtSecurityToken original = new JwtSecurityTokenHandler().ReadJwtToken(f.Capabilities.Issue(job));
        SymmetricSecurityKey key = new(Convert.FromBase64String(f.Settings.SigningKeys["key-v1"])) { KeyId = "key-v1" };
        JwtSecurityTokenHandler handler = new();
        string unsigned = handler.WriteToken(new JwtSecurityToken(new JwtHeader(), original.Payload));
        Assert.IsFalse(f.Capabilities.Verify(unsigned, job, f.Clock.Now));
        JwtPayload altered = new();
        foreach (KeyValuePair<string, object> pair in original.Payload) { altered.Add(pair.Key, pair.Value); }
        altered["operation"] = "other-operation";
        string wrongOperation = handler.WriteToken(new JwtSecurityToken(new JwtHeader(new SigningCredentials(key, SecurityAlgorithms.HmacSha256)), altered));
        Assert.IsFalse(f.Capabilities.Verify(wrongOperation, job, f.Clock.Now));
        SymmetricSecurityKey largerKey = new(Enumerable.Repeat((byte)34, 48).ToArray()) { KeyId = "key-v1" };
        f.Settings.SigningKeys["key-v1"] = Convert.ToBase64String(largerKey.Key);
        string wrongAlgorithm = handler.WriteToken(new JwtSecurityToken(new JwtHeader(new SigningCredentials(largerKey, SecurityAlgorithms.HmacSha384)), original.Payload));
        Assert.IsFalse(f.Capabilities.Verify(wrongAlgorithm, job, f.Clock.Now));
    }

    [TestMethod]
    public void RotationRetainsIssuedCapabilitiesWhileIdentityUsesItsDedicatedStableKey()
    {
        AgentJobFixture f = new(); AgentJob job = f.Intent(); string capability = f.Capabilities.Issue(job);
        f.Settings.SigningKeys["key-v2"] = Convert.ToBase64String(Enumerable.Repeat((byte)51, 32).ToArray());
        f.Settings.ActiveSigningKeyVersion = "key-v2";
        Assert.IsTrue(f.Settings.IsValid()); Assert.AreEqual(job.JobId, f.Intent().JobId);
        Assert.AreEqual(capability, f.Capabilities.Issue(job));
        Assert.IsTrue(f.Capabilities.Verify(capability, job, f.Clock.Now));
        f.Settings.SigningKeys.Remove("key-v1");
        Assert.IsFalse(f.Capabilities.Verify(capability, job, f.Clock.Now));
        Assert.ThrowsExactly<JobException>(() => f.Capabilities.Issue(job));
    }

    [TestMethod]
    public void StableIdentityIsKeyedNamespacedAndNotPayloadDependent()
    {
        AgentJobFixture f = new(); AgentJob job = f.Intent();
        Assert.IsTrue(AgentJob.IsJobId(job.JobId)); Assert.AreEqual(71, job.JobId.Length);
        Assert.AreEqual(f.Owner.WorkspaceKey + ":" + job.JobId[7], job.WorkspaceShard);
        Assert.AreEqual(job.JobId, AgentJob.Identity(f.Owner, job.TargetAgent, job.HandoffId, f.Settings.IdentityKey));
        Assert.AreNotEqual(job.JobId, AgentJob.Identity(f.Writer, job.TargetAgent, job.HandoffId, f.Settings.IdentityKey));
        Assert.AreNotEqual(job.JobId, AgentJob.Identity(f.Owner with { WorkspaceKey = "other" }, job.TargetAgent, job.HandoffId, f.Settings.IdentityKey));
        Assert.AreNotEqual(job.JobId, AgentJob.Identity(f.Owner, "other-v1", job.HandoffId, f.Settings.IdentityKey));
        Assert.AreNotEqual(job.JobId, AgentJob.Identity(f.Owner, job.TargetAgent, "other-event", f.Settings.IdentityKey));
        Assert.AreNotEqual(job.JobId, AgentJob.Identity(f.Owner, job.TargetAgent, job.HandoffId, f.Settings.SigningKeys["key-v1"]));
        Assert.ThrowsExactly<JobException>(() => AgentJob.Identity(f.Owner, job.TargetAgent, job.HandoffId, "weak"));
        foreach (string? id in new[] { null, "", "job_v1_" + new string('G', 64), "job_v1_" + new string('0', 63) })
        { Assert.IsFalse(AgentJob.IsJobId(id)); }
        Assert.ThrowsExactly<JobException>(() => AgentJob.PartitionFor("bad/workspace", job.JobId));
        Assert.IsFalse(AgentJob.IsPrincipal("claimed-agent"));
    }

    [TestMethod]
    public void JobKeysAndLimitsFailClosedWithoutAffectingDisabledStageOne()
    {
        Assert.IsFalse(new TrackedJobOptions().IsValid());
        foreach (string? key in new[] { null, "", "weak", new string('!', 44), Convert.ToBase64String(new byte[31]),
            Convert.ToBase64String(new byte[65]), Convert.ToBase64String(new byte[32]) + "\n" })
        { Assert.IsFalse(TrackedJobOptions.IsKey(key)); }
        Action<TrackedJobOptions>[] mutations =
        [
            o => o.IdentityKey = "invalid", o => o.SigningKeys["key-v1"] = o.IdentityKey,
            o => o.ActiveSigningKeyVersion = "missing", o => o.SigningKeys["bad/key"] = o.SigningKeys["key-v1"],
            o => o.SigningKeys.Clear(), o => o.ResultDeadlineMinutes = 0, o => o.ResultDeadlineMinutes = 10081,
            o => o.CallbackGraceMinutes = 61, o => o.PayloadRetentionDays = 0, o => o.PayloadRetentionDays = 366,
            o => o.DispatchLeaseSeconds = 59, o => o.DispatchLeaseSeconds = 301, o => o.MaxResultBytes = 16385,
            o => o.MaintenanceMaxRowsPerTick = 65, o => o.MaintenanceBudgetSeconds = 46
        ];
        foreach (Action<TrackedJobOptions> mutation in mutations)
        { AgentJobFixture f = new(); Assert.IsTrue(f.Settings.IsValid()); mutation(f.Settings); Assert.IsFalse(f.Settings.IsValid()); }
        AgentGatewayOptions gateway = new(); Assert.IsTrue(gateway.IsValid());
        gateway.EnableTrackedJobs = true; Assert.IsFalse(gateway.IsValid());
        gateway.EnableTrackedJobs = false; gateway.EnableJobMaintenance = true; Assert.IsFalse(gateway.IsValid());
        gateway.EnableTrackedJobAccess = true; Assert.IsTrue(gateway.IsValid());
    }

    [TestMethod]
    public void ResultSchemaIsExplicitBoundedAndCanonicalWithoutNumericCoercion()
    {
        using JsonDocument first = JsonDocument.Parse("{\"summary\":\"Unicode \\u03a9\",\"data\":{\"z\":2,\"n\":900719925474099312345,\"a\":[3,1]}}");
        using JsonDocument second = JsonDocument.Parse("{\"data\":{\"a\":[3,1],\"n\":900719925474099312345,\"z\":2},\"summary\":\"Unicode \\u03a9\"}");
        string json = JobWire.CanonicalResult(JobOutcome.Succeeded, first.RootElement, 16384);
        Assert.AreEqual(json, JobWire.CanonicalResult(JobOutcome.Succeeded, second.RootElement, Encoding.UTF8.GetByteCount(json)));
        Assert.IsTrue(json.Contains("900719925474099312345", StringComparison.Ordinal));
        Assert.AreEqual(JobError.ResultTooLarge, Assert.ThrowsExactly<JobException>(() =>
            JobWire.CanonicalResult(JobOutcome.Succeeded, first.RootElement, Encoding.UTF8.GetByteCount(json) - 1)).Code);
        foreach (string value in new[] { "null", "[]", "{}", "{\"summary\":\" \",\"data\":{}}", "{\"summary\":\"ok\",\"data\":[]}",
            "{\"summary\":\"ok\",\"data\":{},\"url\":\"not-allowed\"}", "{\"summary\":\"ok\",\"data\":{\"x\":1,\"x\":2}}" })
        {
            using JsonDocument bad = JsonDocument.Parse(value);
            Assert.ThrowsExactly<JobException>(() => JobWire.CanonicalResult(JobOutcome.Succeeded, bad.RootElement, 16384));
        }
        using JsonDocument failure = JsonDocument.Parse("{\"code\":\"no-match\",\"message\":\"No approved match\"}");
        Assert.IsNotNull(JobWire.CanonicalResult(JobOutcome.Failed, failure.RootElement, 16384));
        Assert.ThrowsExactly<JobException>(() => JobWire.CanonicalResult((JobOutcome)99, failure.RootElement, 16384));
        Assert.ThrowsExactly<JobException>(() => JobWire.CanonicalResult(JobOutcome.Failed, first.RootElement, 16384));
        Assert.AreNotEqual(JobWire.ResultFingerprint(JobOutcome.Succeeded, json), JobWire.ResultFingerprint(JobOutcome.Failed, json));
    }

    [TestMethod]
    public void RequestParsingRejectsExtraFieldsDuplicateKeysAndUnsupportedContinuation()
    {
        AgentJobFixture f = new();
        JsonElement input = JsonSerializer.SerializeToElement(f.Request, GatewayJson.Options);
        Assert.AreEqual(f.Request.TargetAgent, JobWire.Parse<StartAgentJobRequest>(input).TargetAgent);
        foreach (string raw in new[] { "null", "[]", "{}", "{\"job_id\":\"one\",\"job_id\":\"two\"}", "{\"job_id\":\"one\",\"caller\":\"forged\"}" })
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            Assert.ThrowsExactly<JobException>(() => JobWire.Parse<GetAgentJobRequest>(document.RootElement));
        }
        Assert.ThrowsExactly<JobException>(() => JobWire.CanonicalRequest(f.Request with { CompletionMode = (JobCompletionMode)1 }, f.Gateway));
        Assert.ThrowsExactly<JobException>(() => JobWire.CanonicalRequest(f.Request with { ResultSchema = "user-uploaded" }, f.Gateway));
        string deep = string.Concat(Enumerable.Repeat("{\"x\":", 34)) + "0" + new string('}', 34);
        using JsonDocument depth = JsonDocument.Parse(deep);
        Assert.ThrowsExactly<JobException>(() => JobWire.Parse<GetAgentJobRequest>(depth.RootElement));
        using JsonDocument many = JsonDocument.Parse("[" + string.Join(',', Enumerable.Repeat("0", 8200)) + "]");
        Assert.ThrowsExactly<JobException>(() => JobWire.Parse<GetAgentJobRequest>(many.RootElement));
        JsonElement properties = JsonSerializer.SerializeToElement(Enumerable.Range(0, 1025).ToDictionary(i => "p" + i, i => i));
        Assert.ThrowsExactly<JobException>(() => JobWire.Parse<GetAgentJobRequest>(properties));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<JobOutcome>("1", GatewayJson.Options));
        Assert.AreEqual("\"succeeded\"", JsonSerializer.Serialize(JobOutcome.Succeeded, GatewayJson.Options));
    }

    [TestMethod]
    public void PersistedSchemaCodesAndPhysicalPropertyBudgetsFailClosed()
    {
        AgentJobFixture f = new(); AgentJob row = f.Intent(); row.RequireValid();
        foreach (AgentJob bad in new[]
        {
            row with { Version = 99 }, row with { WorkspaceShard = "wrong" }, row with { ProviderStateCode = 1 },
            row with { ContinuationStateCode = 1 }, row with { DispatchStateCode = 99 }, row with { ResultStateCode = 99 },
            row with { OutcomeCode = 99 }, row with { InitiatorPrincipalKey = "forged" }, row with { RequestJson = null },
            row with { ResultStateCode = (int)JobResultState.Available }, row with { ResultDeadlineUtc = row.CreatedUtc },
            row with { CapabilityExpiresUtc = row.ResultDeadlineUtc }, row with { Attempts = -1 }, row with { ClaimGeneration = -1 }
        }) { Assert.ThrowsExactly<JobException>(bad.RequireValid); }
        AgentJob large = row with { RequestJson = new string('x', 24577) };
        Assert.AreEqual(JobError.InputTooLarge, Assert.ThrowsExactly<JobException>(large.RequireValid).Code);
        AgentJob purged = row with { ResultStateCode = (int)JobResultState.Purged, RequestJson = null };
        purged.RequireValid(); Assert.ThrowsExactly<JobException>(() => JobWire.Prepare(purged, f.Capabilities, 65536));
        Assert.ThrowsExactly<JobException>(() => JobWire.Prepare(row, f.Capabilities, 1));
        CollectionAssert.AreEqual(JobWire.Prepare(row, f.Capabilities, 65536).Body.ToArray(), JobWire.Prepare(row, f.Capabilities, 65536).Body.ToArray());
    }
}
