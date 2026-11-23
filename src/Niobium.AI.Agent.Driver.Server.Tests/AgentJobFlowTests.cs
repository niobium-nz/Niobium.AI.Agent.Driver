using System.Text.Json;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Server.Tests;

[TestClass]
public sealed class AgentJobFlowTests
{
    [TestMethod]
    public async Task DurableStartPrecedesSendAndIdenticalStartsDoNotSendAgain()
    {
        AgentJobFixture f = new(); await f.SeedAsync();
        f.Provider.OnDispatch = async prepared =>
        {
            using JsonDocument body = JsonDocument.Parse(prepared.Body);
            using JsonDocument envelope = JsonDocument.Parse(body.RootElement.GetProperty("input").GetString()!);
            string id = envelope.RootElement.GetProperty("job_id").GetString()!;
            AgentJob stored = await f.ReadAsync(id);
            Assert.AreEqual(JobDispatchState.Sending, (JobDispatchState)stored.DispatchStateCode);
            Assert.IsNotNull(stored.ETag);
            Assert.IsTrue(f.Capabilities.Verify(envelope.RootElement.GetProperty("completion_capability").GetString()!, stored, f.Clock.Now));
        };
        AgentJobStatus first = await f.Flow.StartAsync(f.Request, f.Owner, default);
        AgentJobStatus second = await f.Flow.StartAsync(f.Request, f.Owner, default);
        Assert.AreEqual(first, second); Assert.AreEqual(JobDispatchState.Accepted, first.DispatchState);
        Assert.AreEqual(JobResultState.Pending, first.ResultState); Assert.AreEqual("unobserved", first.ProviderState);
        Assert.AreEqual("not_requested", first.ContinuationState); Assert.AreEqual(1, f.Provider.Calls.Count);
        string publicJson = JsonSerializer.Serialize(first, GatewayJson.Options);
        Assert.IsFalse(publicJson.Contains("capability", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(publicJson.Contains("agtch_", StringComparison.Ordinal));
        Assert.IsFalse(publicJson.Contains(f.Request.Input.Task, StringComparison.Ordinal));
        JobException conflict = await Assert.ThrowsExactlyAsync<JobException>(() => f.Flow.StartAsync(
            f.Request with { Input = f.Request.Input with { Task = "different" } }, f.Owner, default));
        Assert.AreEqual(JobError.JobConflict, conflict.Code); Assert.AreEqual(1, f.Provider.Calls.Count);
        AgentJobResult pending = await f.Flow.ResultAsync(new() { JobId = first.JobId }, f.Owner, default);
        Assert.IsNull(pending.Result);
    }

    [TestMethod]
    public async Task ConcurrentSameIdStartsHaveOneSenderAndReturnThePersistedIdentity()
    {
        AgentJobFixture f = new(); await f.SeedAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Provider.OnDispatch = async _ => { entered.SetResult(); await release.Task; };
        Task<AgentJobStatus> first = f.Flow.StartAsync(f.Request, f.Owner, default);
        await entered.Task;
        AgentJobStatus other = await f.Flow.StartAsync(f.Request, f.Owner, default);
        Assert.AreEqual(JobDispatchState.Sending, other.DispatchState);
        release.SetResult();
        Assert.AreEqual((await first).JobId, other.JobId); Assert.AreEqual(1, f.Provider.Calls.Count);
    }

    [TestMethod]
    public async Task CallbackBeforeDispatchResponseIsPreservedAndCompletionIsImmutable()
    {
        AgentJobFixture f = new(); await f.SeedAsync();
        f.Provider.OnDispatch = async _ =>
        {
            string id = AgentJob.Identity(f.Owner, f.Request.TargetAgent, f.Request.HandoffId, f.Settings.IdentityKey);
            AgentJob sending = await f.ReadAsync(id);
            JobResultReceipt receipt = await f.Flow.SubmitAsync(f.Completion(sending), f.Writer, default);
            Assert.IsFalse(receipt.Duplicate);
        };
        AgentJobStatus started = await f.Flow.StartAsync(f.Request, f.Owner, default);
        Assert.AreEqual(JobDispatchState.Accepted, started.DispatchState); Assert.AreEqual(JobResultState.Available, started.ResultState);
        AgentJob stored = await f.ReadAsync(started.JobId);
        JobResultReceipt duplicate = await f.Flow.SubmitAsync(f.Completion(stored), f.Writer, default);
        Assert.IsTrue(duplicate.Duplicate); Assert.AreEqual(stored.ResultReceivedUtc, duplicate.ResultReceivedUtc);
        AgentJobResult result = await f.Flow.ResultAsync(new() { JobId = started.JobId }, f.Owner, default);
        Assert.AreEqual(JobOutcome.Succeeded, result.Outcome); Assert.AreEqual(42, result.Result!.Value.GetProperty("data").GetProperty("value").GetInt32());
        Assert.AreEqual(stored.ResultHash, result.ResultHash);
        await Assert.ThrowsExactlyAsync<JobException>(() => f.Flow.SubmitAsync(f.Completion(stored, JobOutcome.Failed), f.Writer, default));
        await f.Flow.StartAsync(f.Request, f.Owner, default); Assert.AreEqual(1, f.Provider.Calls.Count);
        // Stop new tracked starts without breaking issued capabilities or owner reads.
        f.Gateway.EnableTrackedJobs = false;
        await Assert.ThrowsExactlyAsync<JobException>(() => f.Flow.StartAsync(f.Request, f.Owner, default));
        Assert.IsTrue((await f.Flow.SubmitAsync(f.Completion(stored), f.Writer, default)).Duplicate);
        Assert.AreEqual(JobResultState.Available, (await f.Flow.StatusAsync(new() { JobId = stored.JobId }, f.Owner, default)).ResultState);
    }

    [TestMethod]
    [DataRow(DispatchState.OutcomeUnknown, JobDispatchState.Unknown)]
    [DataRow(DispatchState.Rejected, JobDispatchState.Rejected)]
    [DataRow(DispatchState.NotDispatched, JobDispatchState.Held)]
    public async Task NonAcceptanceNeverBecomesAnotherAutomaticSend(DispatchState result, JobDispatchState expected)
    {
        AgentJobFixture f = new(); await f.SeedAsync(); f.Provider.Outcome = result;
        AgentJobStatus first = await f.Flow.StartAsync(f.Request, f.Owner, default);
        Assert.AreEqual(expected, first.DispatchState);
        await f.Flow.StartAsync(f.Request, f.Owner, default); Assert.AreEqual(1, f.Provider.Calls.Count);
    }

    [TestMethod]
    public async Task CrashAfterClaimIsHeldAfterExpiryAndNeverResent()
    {
        AgentJobFixture f = new(); await f.SeedAsync();
        f.Provider.OnDispatch = _ => throw new IOException("private simulated provider failure");
        await Assert.ThrowsExactlyAsync<IOException>(() => f.Flow.StartAsync(f.Request, f.Owner, default));
        string id = AgentJob.Identity(f.Owner, f.Request.TargetAgent, f.Request.HandoffId, f.Settings.IdentityKey);
        AgentJob sending = await f.ReadAsync(id);
        Assert.AreEqual(JobDispatchState.Sending, (JobDispatchState)sending.DispatchStateCode);
        f.Clock.Now = f.Clock.Now.AddMinutes(2);
        AgentJob held = await f.Domain(id).MaintainAsync(f.Clock.Now, 30, default);
        Assert.AreEqual(JobDispatchState.Held, (JobDispatchState)held.DispatchStateCode);
        await f.Flow.StartAsync(f.Request, f.Owner, default); Assert.AreEqual(1, f.Provider.Calls.Count);
        // Valid callbacks remain useful even when dispatch outcome is ambiguous.
        Assert.AreEqual(JobResultState.Available, (await f.Flow.SubmitAsync(f.Completion(held), f.Writer, default)).ResultState);
    }

    [TestMethod]
    public async Task CurrentVersionTwoGrantsAndImmutableBindingsAreRequired()
    {
        AgentJobFixture f = new(); await f.SeedAsync();
        AgentJob row = await f.RegisterAsync();
        AgentGrant ownerGrant = (await f.Legacy.Grants.RetrieveAsync(AgentGrant.PartitionFor(f.Owner), row.TargetAgent))!;
        await f.Legacy.Grants.UpdateAsync([ownerGrant with { Version = 1 }]);
        await Assert.ThrowsExactlyAsync<GatewayException>(() => f.Flow.DispatchRegisteredAsync(row, default));
        ownerGrant = (await f.Legacy.Grants.RetrieveAsync(ownerGrant.CallerPartition, row.TargetAgent))!;
        await f.Legacy.Grants.UpdateAsync([ownerGrant with { Version = 2, CanStartJob = false }]);
        await Assert.ThrowsExactlyAsync<GatewayException>(() => f.Flow.StartAsync(f.Request, f.Owner, default));
        ownerGrant = (await f.Legacy.Grants.RetrieveAsync(ownerGrant.CallerPartition, row.TargetAgent))!;
        await f.Legacy.Grants.UpdateAsync([ownerGrant with { CanStartJob = true }]);
        AgentRegistration target = (await f.Legacy.Registrations.RetrieveAsync(f.Owner.WorkspaceKey, row.TargetAgent))!;
        await f.Legacy.Registrations.UpdateAsync([target with { ApiChannelId = "agtch_changed" }]);
        await Assert.ThrowsExactlyAsync<JobException>(() => f.Flow.DispatchRegisteredAsync(row, default));
        Assert.AreEqual(0, f.Provider.Calls.Count);
        await f.Legacy.Registrations.DeleteAsync([target]);
        await Assert.ThrowsExactlyAsync<JobException>(() => f.Flow.StartAsync(f.Request, f.Owner, default));
    }

    [TestMethod]
    public async Task JobIdAloneCannotReadOrCompleteAndRevocationTakesEffectImmediately()
    {
        AgentJobFixture f = new(); await f.SeedAsync(); AgentJob row = await f.RegisterAsync();
        await Assert.ThrowsExactlyAsync<JobException>(() => f.Flow.ResultAsync(new() { JobId = row.JobId }, f.Writer, default));
        await Assert.ThrowsExactlyAsync<JobException>(() => f.Flow.StatusAsync(new() { JobId = row.JobId }, f.Writer, default));
        await Assert.ThrowsExactlyAsync<JobException>(() => f.Flow.SubmitAsync(f.Completion(row), f.Owner, default));
        await Assert.ThrowsExactlyAsync<JobException>(() => f.Flow.SubmitAsync(f.Completion(row) with { CompletionCapability = "forged" }, f.Writer, default));
        await Assert.ThrowsExactlyAsync<GatewayException>(() => f.Flow.StatusAsync(new() { JobId = row.JobId }, f.Owner with { WorkspaceKey = "other" }, default));
        AgentGrant writer = (await f.Legacy.Grants.RetrieveAsync(AgentGrant.PartitionFor(f.Writer), row.TargetAgent))!;
        await f.Legacy.Grants.DeleteAsync([writer]);
        await Assert.ThrowsExactlyAsync<GatewayException>(() => f.Flow.SubmitAsync(f.Completion(row), f.Writer, default));
        Assert.AreEqual(JobResultState.Pending, (JobResultState)(await f.ReadAsync(row.JobId)).ResultStateCode);
        await Assert.ThrowsExactlyAsync<JobException>(() => f.Flow.StatusAsync(new() { JobId = "bad" }, f.Owner, default));
        await Assert.ThrowsExactlyAsync<JobException>(() => f.Flow.ResultAsync(new() { JobId = "job_v1_" + new string('0', 64) }, f.Owner, default));
    }

    [TestMethod]
    public async Task DisabledFeaturesInvalidKeysAndOversizedEnvelopesDoNotCreateJobs()
    {
        AgentJobFixture f = new(); await f.SeedAsync();
        f.Gateway.EnableTrackedJobs = false;
        await Assert.ThrowsExactlyAsync<JobException>(() => f.Flow.StartAsync(f.Request, f.Owner, default));
        f.Gateway.EnableTrackedJobs = true; f.Settings.IdentityKey = "invalid";
        await Assert.ThrowsExactlyAsync<JobException>(() => f.Flow.StartAsync(f.Request, f.Owner, default));
        Assert.AreEqual(0, f.Jobs.Writes); Assert.AreEqual(0, f.Provider.Calls.Count);
        AgentJobFixture tiny = new(); await tiny.SeedAsync(); tiny.Gateway.MaxProviderBodyBytes = 1024;
        await Assert.ThrowsExactlyAsync<JobException>(() => tiny.Flow.StartAsync(tiny.Request, tiny.Owner, default));
        Assert.AreEqual(0, tiny.Jobs.Writes);
    }

    [TestMethod]
    public async Task ConversationReuseAndTargetInputLimitAreIndependentlyEnforced()
    {
        AgentJobFixture f = new(); await f.SeedAsync();
        StartAgentJobRequest request = f.Request with { ConversationKey = "parent-thread" };
        await Assert.ThrowsExactlyAsync<GatewayException>(() => f.Flow.StartAsync(request, f.Owner, default));
        f.Gateway.AllowConversationReuse = true;
        await Assert.ThrowsExactlyAsync<GatewayException>(() => f.Flow.StartAsync(request, f.Owner, default));
        AgentRegistration target = (await f.Legacy.Registrations.RetrieveAsync(f.Owner.WorkspaceKey, request.TargetAgent))!;
        await f.Legacy.Registrations.UpdateAsync([target with { AllowConversationReuse = true }]);
        AgentJobStatus started = await f.Flow.StartAsync(request, f.Owner, default);
        AgentJob row = await f.ReadAsync(started.JobId);
        Assert.IsNotNull(row.ProviderConversationKey); Assert.AreNotEqual(request.ConversationKey, row.ProviderConversationKey);
        using JsonDocument body = JsonDocument.Parse(f.Provider.Calls.Single().Body);
        Assert.AreEqual(row.ProviderConversationKey, body.RootElement.GetProperty("conversation_key").GetString());
        target = (await f.Legacy.Registrations.RetrieveAsync(f.Owner.WorkspaceKey, request.TargetAgent))!;
        await f.Legacy.Registrations.UpdateAsync([target with { MaxInputBytes = 1 }]);
        await Assert.ThrowsExactlyAsync<GatewayException>(() => f.Flow.StartAsync(request with { HandoffId = "oversize" }, f.Owner, default));
    }
}
