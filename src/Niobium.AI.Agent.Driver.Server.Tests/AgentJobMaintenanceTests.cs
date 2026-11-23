using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Server.Tests;

[TestClass]
public sealed class AgentJobMaintenanceTests
{
    [TestMethod]
    public async Task SweepIsBoundedPersistsItsCursorWrapsAndRecoversOnlyNeverSentJobs()
    {
        AgentJobFixture f = new(); await f.SeedAsync(); f.Settings.MaintenanceMaxRowsPerTick = 2;
        AgentJob first = await f.RegisterAsync("never-sent-1");
        AgentJob second = await f.RegisterAsync("never-sent-2");
        AgentJob uncertain = await f.RegisterAsync("uncertain");
        AgentJobDomain domain = f.Domain(uncertain.JobId);
        await domain.ClaimDispatchAsync(Guid.NewGuid().ToString("N"), f.Clock.Now, 60, default);
        f.Clock.Now = f.Clock.Now.AddMinutes(2);
        for (int tick = 0; tick < 24; tick++)
        {
            var report = await f.Maintenance.ExecuteAsync(default);
            Assert.IsTrue(report.Pages <= 2); Assert.IsTrue(report.Rows <= 2);
            Assert.AreEqual(0, report.Failures);
        }
        Assert.AreEqual(JobDispatchState.Accepted, (JobDispatchState)(await f.ReadAsync(first.JobId)).DispatchStateCode);
        Assert.AreEqual(JobDispatchState.Accepted, (JobDispatchState)(await f.ReadAsync(second.JobId)).DispatchStateCode);
        Assert.AreEqual(JobDispatchState.Held, (JobDispatchState)(await f.ReadAsync(uncertain.JobId)).DispatchStateCode);
        Assert.AreEqual(2, f.Provider.Calls.Count);
        AgentMaintenance cursor = (await f.Cursors.RetrieveAsync(f.Owner.WorkspaceKey, "jobs-v1"))!;
        Assert.IsNull(cursor.LeaseOwner); Assert.IsTrue(cursor.Generation > 1);
        await f.Maintenance.ExecuteAsync(default); Assert.AreEqual(2, f.Provider.Calls.Count);
    }

    [TestMethod]
    public async Task ActiveLeaseAndStaleWorkerCannotAdvanceOrReleaseAnotherSweep()
    {
        AgentJobFixture f = new();
        AgentMaintenanceDomain domain = await f.MaintenanceDomains.GetAsync(f.Owner.WorkspaceKey, "jobs-v1");
        string owner = Guid.NewGuid().ToString("N");
        AgentMaintenance claim = (await domain.ClaimAsync(f.Owner.WorkspaceKey, owner, f.Clock.Now, default))!;
        Assert.IsNull(await domain.ClaimAsync(f.Owner.WorkspaceKey, Guid.NewGuid().ToString("N"), f.Clock.Now, default));
        Assert.AreEqual(0, (await f.Maintenance.ExecuteAsync(default)).Pages);
        Assert.IsNull(await domain.CheckpointAsync(claim with { LeaseOwner = "foreign" }, "row", f.Clock.Now, false, default));
        Assert.IsNull(await domain.CheckpointAsync(claim with { Generation = 99 }, "row", f.Clock.Now, true, default));
        f.Clock.Now = f.Clock.Now.AddSeconds(90);
        Assert.IsNull(await domain.CheckpointAsync(claim, "row", f.Clock.Now, false, default));
        AgentMaintenance next = (await domain.ClaimAsync(f.Owner.WorkspaceKey, Guid.NewGuid().ToString("N"), f.Clock.Now, default))!;
        Assert.IsTrue(next.Generation > claim.Generation);
        Assert.IsNull(await domain.CheckpointAsync(claim, "old", f.Clock.Now, false, default));
        AgentMaintenance checkpoint = (await domain.CheckpointAsync(next, "row-key", f.Clock.Now, false, default))!;
        Assert.AreEqual("row-key", checkpoint.Cursor); Assert.AreEqual(0, checkpoint.Shard);
        AgentMaintenance wrapped = (await domain.CheckpointAsync(checkpoint, null, f.Clock.Now, false, default))!;
        Assert.IsNull(wrapped.Cursor); Assert.AreEqual(1, wrapped.Shard);
        Assert.IsNotNull(await domain.CheckpointAsync(wrapped, null, f.Clock.Now, true, default));
    }

    [TestMethod]
    public async Task PoisonRowsAndRevokedGrantsDoNotStarveLaterRows()
    {
        AgentJobFixture f = new(); await f.SeedAsync();
        AgentJob bad = await f.RegisterAsync("poison"); AgentJob valid = await f.RegisterAsync("good");
        f.Jobs.Mutate(bad.WorkspaceShard, bad.JobId, row => row with { Version = 999 });
        int failures = 0;
        for (int tick = 0; tick < 3; tick++) { failures += (await f.Maintenance.ExecuteAsync(default)).Failures; }
        Assert.IsTrue(failures > 0); Assert.AreEqual(1, f.Provider.Calls.Count);
        Assert.AreEqual(JobDispatchState.Accepted, (JobDispatchState)(await f.ReadAsync(valid.JobId)).DispatchStateCode);
        AgentJob denied = await f.RegisterAsync("revoked");
        await f.Legacy.Grants.DeleteAsync([f.Legacy.Grant]);
        for (int tick = 0; tick < 3; tick++) { failures += (await f.Maintenance.ExecuteAsync(default)).Failures; }
        Assert.AreEqual(1, f.Provider.Calls.Count);
        Assert.AreEqual(JobDispatchState.Registered, (JobDispatchState)(await f.ReadAsync(denied.JobId)).DispatchStateCode);
    }

    [TestMethod]
    public async Task DisabledMaintenanceNeverReadsAndRollbackKeepsDeadlineMaintenanceWithoutNewStarts()
    {
        foreach (int flag in new[] { 0, 1, 2 })
        {
            AgentJobFixture off = new();
            if (flag == 0) { off.Gateway.Enabled = false; }
            if (flag == 1) { off.Gateway.EnableTrackedJobAccess = false; }
            if (flag == 2) { off.Gateway.EnableJobMaintenance = false; }
            Assert.AreEqual(0, (await off.Maintenance.ExecuteAsync(default)).Rows);
            Assert.AreEqual(0, off.Jobs.Reads); Assert.AreEqual(0, off.Cursors.Reads);
        }
        AgentJobFixture f = new(); await f.SeedAsync(); AgentJob row = await f.RegisterAsync();
        f.Gateway.EnableTrackedJobs = false;
        for (int i = 0; i < 2; i++) { await f.Maintenance.ExecuteAsync(default); }
        Assert.AreEqual(0, f.Provider.Calls.Count);
        f.Clock.Now = row.ResultDeadlineUtc;
        for (int i = 0; i < 2; i++) { await f.Maintenance.ExecuteAsync(default); }
        Assert.AreEqual(JobResultState.DeadlineExceeded, (JobResultState)(await f.ReadAsync(row.JobId)).ResultStateCode);
        Assert.AreEqual(0, f.Provider.Calls.Count);
    }

    [TestMethod]
    public async Task CursorWriteResponseLossIsReconciledAndConflictsRemainBounded()
    {
        AgentJobFixture f = new();
        AgentMaintenanceDomain domain = await f.MaintenanceDomains.GetAsync(f.Owner.WorkspaceKey, "jobs-v1");
        f.Cursors.LoseResponses = 1;
        AgentMaintenance claim = (await domain.ClaimAsync(f.Owner.WorkspaceKey, Guid.NewGuid().ToString("N"), f.Clock.Now, default))!;
        Assert.IsNotNull(claim.ETag);
        f.Cursors.LoseResponses = 1;
        AgentMaintenance checkpoint = (await domain.CheckpointAsync(claim, "row", f.Clock.Now, false, default))!;
        Assert.AreEqual("row", checkpoint.Cursor);
        f.Cursors.AfterStore = row => row.WriteId = "foreign-write";
        Assert.IsNull(await domain.CheckpointAsync(checkpoint, null, f.Clock.Now, false, default));
        AgentJobFixture dropped = new(); dropped.Cursors.DropWrites = true;
        AgentMaintenanceDomain missing = await dropped.MaintenanceDomains.GetAsync(dropped.Owner.WorkspaceKey, "jobs-v1");
        await Assert.ThrowsExactlyAsync<JobException>(() => missing.ClaimAsync(dropped.Owner.WorkspaceKey, Guid.NewGuid().ToString("N"), dropped.Clock.Now, default));
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.ClaimAsync("other", Guid.NewGuid().ToString("N"), f.Clock.Now, default));
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.ClaimAsync(f.Owner.WorkspaceKey, "bad-owner", f.Clock.Now, default));
        f.Cursors.Mutate(f.Owner.WorkspaceKey, "jobs-v1", row => row with { Version = 99 });
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.ClaimAsync(f.Owner.WorkspaceKey, Guid.NewGuid().ToString("N"), f.Clock.Now, default));
    }

    [TestMethod]
    public async Task InvalidConfigurationAndCancellationCannotStartRecoveryWork()
    {
        AgentJobFixture f = new(); f.Settings.MaintenanceMaxRowsPerTick = 65;
        await Assert.ThrowsExactlyAsync<JobException>(() => f.Maintenance.ExecuteAsync(default));
        AgentJobFixture canceled = new(); using CancellationTokenSource source = new(); source.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => canceled.Maintenance.ExecuteAsync(source.Token));
        Assert.AreEqual(0, canceled.Provider.Calls.Count);
    }
}
