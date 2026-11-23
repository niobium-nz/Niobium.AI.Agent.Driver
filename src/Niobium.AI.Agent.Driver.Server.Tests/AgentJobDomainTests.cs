using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Server.Tests;

[TestClass]
public sealed class AgentJobDomainTests
{
    [TestMethod]
    public async Task LostWriteResponsesAreReconciledWithoutAdoptingForeignClaims()
    {
        AgentJobFixture f = new(); f.Jobs.LoseResponses = 1;
        AgentJob row = await f.RegisterAsync(); Assert.IsNotNull(row.ETag);
        AgentJobDomain domain = f.Domain(row.JobId);
        f.Jobs.LoseResponses = 1;
        AgentJob claim = (await domain.ClaimDispatchAsync(Guid.NewGuid().ToString("N"), f.Clock.Now, 60, default))!;
        Assert.IsNotNull(claim); Assert.AreNotEqual(row.ETag, claim.ETag);
        f.Jobs.LoseResponses = 1;
        JobResultReceipt result = await domain.SubmitAsync(f.Writer, f.Completion(row), f.Capabilities, f.Settings, f.Clock.Now, default);
        Assert.AreEqual(JobResultState.Available, result.ResultState);
        f.Jobs.LoseResponses = 1;
        AgentJob accepted = await domain.RecordDispatchAsync(claim, new(row.TargetAgent, row.HandoffId, DispatchState.Accepted), f.Clock.Now, default);
        Assert.AreEqual(JobDispatchState.Accepted, (JobDispatchState)accepted.DispatchStateCode);
        Assert.AreEqual(result.ResultHash, accepted.ResultHash);

        AgentJob other = await f.RegisterAsync("lost-claim");
        f.Jobs.AfterStore = stored => { stored.WriteId = "foreign-write"; stored.LeaseOwner = Guid.NewGuid().ToString("N"); };
        Assert.IsNull(await f.Domain(other.JobId).ClaimDispatchAsync(Guid.NewGuid().ToString("N"), f.Clock.Now, 60, default));
        Assert.AreEqual(0, f.Provider.Calls.Count);
    }

    [TestMethod]
    public async Task ConcurrentCallbacksAndDispatchSaveMergeCurrentStateNotStaleSnapshots()
    {
        AgentJobFixture f = new(); AgentJob row = await f.RegisterAsync(); AgentJobDomain domain = f.Domain(row.JobId);
        AgentJob claim = (await domain.ClaimDispatchAsync(Guid.NewGuid().ToString("N"), f.Clock.Now, 60, default))!;
        f.Jobs.BeforeUpdate = _ => domain.SubmitAsync(f.Writer, f.Completion(row), f.Capabilities, f.Settings, f.Clock.Now, default).GetAwaiter().GetResult();
        AgentJob accepted = await domain.RecordDispatchAsync(claim, new(row.TargetAgent, row.HandoffId, DispatchState.Accepted), f.Clock.Now, default);
        Assert.AreEqual(JobResultState.Available, (JobResultState)accepted.ResultStateCode);
        Assert.AreEqual(JobDispatchState.Accepted, (JobDispatchState)accepted.DispatchStateCode);
        JobResultReceipt[] duplicates = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            Task.Run(() => f.Domain(row.JobId).SubmitAsync(f.Writer, f.Completion(row), f.Capabilities, f.Settings, f.Clock.Now, default))));
        Assert.IsTrue(duplicates.All(r => r.Duplicate));
    }

    [TestMethod]
    public async Task GenerationOwnerAndLeaseFenceLateDispatchWriters()
    {
        AgentJobFixture f = new(); AgentJob row = await f.RegisterAsync(); AgentJobDomain domain = f.Domain(row.JobId);
        AgentJob claim = (await domain.ClaimDispatchAsync(Guid.NewGuid().ToString("N"), f.Clock.Now, 60, default))!;
        DispatchAgentResult accepted = new(row.TargetAgent, row.HandoffId, DispatchState.Accepted);
        foreach (AgentJob stale in new[] { claim with { LeaseOwner = "other" }, claim with { ClaimGeneration = 99 }, claim with { JobId = "other" } })
        { Assert.AreEqual(JobDispatchState.Sending, (JobDispatchState)(await domain.RecordDispatchAsync(stale, accepted, f.Clock.Now, default)).DispatchStateCode); }
        f.Clock.Now = f.Clock.Now.AddSeconds(60);
        Assert.AreEqual(JobDispatchState.Sending, (JobDispatchState)(await domain.RecordDispatchAsync(claim, accepted, f.Clock.Now, default)).DispatchStateCode);
        AgentJob held = await domain.MaintainAsync(f.Clock.Now, 30, default);
        Assert.AreEqual(JobDispatchState.Held, (JobDispatchState)held.DispatchStateCode);
        Assert.IsNull(await domain.ClaimDispatchAsync(Guid.NewGuid().ToString("N"), f.Clock.Now, 60, default));
    }

    [TestMethod]
    public async Task DeadlineLateArrivalRetentionAndTombstoneKeepTheirIndependentMeaning()
    {
        AgentJobFixture f = new(); AgentJob row = await f.RegisterAsync(); AgentJobDomain domain = f.Domain(row.JobId);
        AgentJob claim = (await domain.ClaimDispatchAsync(Guid.NewGuid().ToString("N"), f.Clock.Now, 60, default))!;
        await domain.RecordDispatchAsync(claim, new(row.TargetAgent, row.HandoffId, DispatchState.Accepted), f.Clock.Now, default);
        f.Clock.Now = row.ResultDeadlineUtc;
        AgentJob overdue = await domain.MaintainAsync(f.Clock.Now, 30, default);
        Assert.AreEqual(JobResultState.DeadlineExceeded, (JobResultState)overdue.ResultStateCode);
        f.Clock.Now = f.Clock.Now.AddSeconds(1);
        await domain.SubmitAsync(f.Writer, f.Completion(row), f.Capabilities, f.Settings, f.Clock.Now, default);
        AgentJob available = await f.ReadAsync(row.JobId); Assert.IsTrue(available.LateArrival);
        f.Clock.Now = available.PayloadPurgeAfterUtc!.Value;
        AgentJob purged = await domain.MaintainAsync(f.Clock.Now, 30, default);
        Assert.AreEqual(JobResultState.Purged, (JobResultState)purged.ResultStateCode);
        Assert.IsNull(purged.RequestJson); Assert.IsNull(purged.ResultJson); Assert.IsNull(purged.Result().Result);
        Assert.AreEqual(available.ResultHash, purged.ResultHash);
        Assert.AreEqual(row.JobId, (await domain.RegisterAsync(row with { ETag = null }, default)).JobId);
        Assert.IsNull(await domain.ClaimDispatchAsync(Guid.NewGuid().ToString("N"), f.Clock.Now, 60, default));
        Assert.AreEqual(JobError.CompletionExpired, (await Assert.ThrowsExactlyAsync<JobException>(() => domain.SubmitAsync(
            f.Writer, f.Completion(row), f.Capabilities, f.Settings, f.Clock.Now, default))).Code);
    }

    [TestMethod]
    public async Task MissingResultsCloseButUnknownOrHeldPayloadsAreNotPurged()
    {
        foreach (DispatchState outcome in new[] { DispatchState.Accepted, DispatchState.OutcomeUnknown })
        {
            AgentJobFixture f = new(); AgentJob row = await f.RegisterAsync(); AgentJobDomain domain = f.Domain(row.JobId);
            AgentJob claim = (await domain.ClaimDispatchAsync(Guid.NewGuid().ToString("N"), f.Clock.Now, 60, default))!;
            await domain.RecordDispatchAsync(claim, new(row.TargetAgent, row.HandoffId, outcome), f.Clock.Now, default);
            f.Clock.Now = row.CapabilityExpiresUtc;
            AgentJob closed = await domain.MaintainAsync(f.Clock.Now, 30, default);
            Assert.AreEqual(outcome == DispatchState.Accepted ? JobResultState.ClosedWithoutResult : JobResultState.DeadlineExceeded,
                (JobResultState)closed.ResultStateCode);
            f.Clock.Now = f.Clock.Now.AddDays(31);
            AgentJob later = await domain.MaintainAsync(f.Clock.Now, 30, default);
            Assert.AreEqual(outcome == DispatchState.Accepted, later.RequestJson is null);
        }
        AgentJobFixture unsent = new(); AgentJob never = await unsent.RegisterAsync(); unsent.Clock.Now = never.ResultDeadlineUtc;
        AgentJob rejected = await unsent.Domain(never.JobId).MaintainAsync(unsent.Clock.Now, 30, default);
        Assert.AreEqual(JobDispatchState.Rejected, (JobDispatchState)rejected.DispatchStateCode);
        Assert.AreEqual(JobResultState.DeadlineExceeded, (JobResultState)rejected.ResultStateCode);
    }

    [TestMethod]
    public async Task UnknownSchemasBadKeysAndBoundedStorageFailuresFailClosed()
    {
        AgentJobFixture f = new(); AgentJob intended = f.Intent(); AgentJobDomain domain = f.Domain(intended.JobId);
        f.Jobs.DropWrites = true;
        Assert.AreEqual(JobError.StorageConflict, (await Assert.ThrowsExactlyAsync<JobException>(() => domain.RegisterAsync(intended, default))).Code);
        f.Jobs.DropWrites = false; AgentJob row = await domain.RegisterAsync(intended, default);
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.RegisterAsync(row, default));
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.ClaimDispatchAsync("not-an-owner", f.Clock.Now, 60, default));
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.ClaimDispatchAsync(Guid.NewGuid().ToString("N"), f.Clock.Now, 1, default));
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.MaintainAsync(f.Clock.Now, 0, default));
        f.Jobs.DropWrites = true;
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.ClaimDispatchAsync(Guid.NewGuid().ToString("N"), f.Clock.Now, 60, default));
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.SubmitAsync(f.Writer, f.Completion(row), f.Capabilities, f.Settings, f.Clock.Now, default));
        f.Jobs.DropWrites = false;
        f.Jobs.Mutate(row.WorkspaceShard, row.JobId, value => value with { Version = 99 });
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.ReadCurrentAsync(default));
        f.Jobs.Mutate(row.WorkspaceShard, row.JobId, value => value with { Version = 1, DispatchStateCode = 99 });
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.ReadCurrentAsync(default));
        await Assert.ThrowsExactlyAsync<JobException>(() => f.Domain(f.Intent("missing").JobId).ClaimDispatchAsync(Guid.NewGuid().ToString("N"), f.Clock.Now, 60, default));
    }

    [TestMethod]
    public async Task VersionOneAndTwoConfigurationRoundTripWithoutGrantingImplicitJobPermission()
    {
        AgentJobFixture f = new();
        AgentRegistrationDomain registrations = f.Legacy.RegistrationDomain();
        AgentRegistration original = await registrations.ConfigureAsync(f.Legacy.Registration, null, f.Gateway);
        AgentRegistration upgraded = await registrations.ConfigureAsync(f.Registration, original.ETag, f.Gateway);
        Assert.AreEqual(2, upgraded.Version);
        await Assert.ThrowsExactlyAsync<GatewayException>(() => registrations.ConfigureAsync(upgraded with { ResultWriterPrincipalKey = f.Owner.PrincipalKey }, upgraded.ETag, f.Gateway));
        await Assert.ThrowsExactlyAsync<GatewayException>(() => registrations.ConfigureAsync(original, upgraded.ETag, f.Gateway));
        AgentGrantDomain grants = f.Legacy.GrantDomain();
        await Assert.ThrowsExactlyAsync<GatewayException>(() => grants.ConfigureAsync(f.Legacy.Grant with { CanStartJob = true }, null, f.Gateway));
        AgentGrant v1 = await grants.ConfigureAsync(f.Legacy.Grant, null, f.Gateway);
        grants.Initialize(v1.CallerPartition, v1.TargetAgent);
        Assert.IsFalse(await grants.PermitsJobAsync(f.Owner, false, f.Clock.Now, default));
        AgentGrant v2 = await grants.ConfigureAsync(v1 with { Version = 2, CanStartJob = true }, v1.ETag, f.Gateway);
        Assert.IsTrue(await grants.PermitsJobAsync(f.Owner, false, f.Clock.Now, default));
        Assert.IsTrue(await grants.PermitsDispatchAsync(f.Owner, f.Clock.Now, default));
        Assert.IsFalse(await grants.PermitsJobAsync(f.Owner, true, f.Clock.Now, default));
        await Assert.ThrowsExactlyAsync<GatewayException>(() => grants.ConfigureAsync(v1, v2.ETag, f.Gateway));
    }
}
