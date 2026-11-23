using System.Diagnostics;
using Microsoft.Extensions.Options;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Core.Flows;

public sealed record JobMaintenanceReport(int Pages, int Rows, int Actions, int Failures, long DurationMs);

/// <summary>Bounded, restart-safe store-only maintenance. It never redispatches an expired Sending/Unknown/Held job.</summary>
public sealed class AgentJobMaintenanceFlow(
    IDomainRepository<AgentMaintenanceDomain, AgentMaintenance> cursors,
    IDomainRepository<AgentJobDomain, AgentJob> jobs, AgentJobFlow dispatch,
    IOptions<AgentGatewayOptions> gateway, IOptions<TrackedJobOptions> settings, TimeProvider clock)
{
    public async Task<JobMaintenanceReport> ExecuteAsync(CancellationToken cancellationToken)
    {
        AgentGatewayOptions options = gateway.Value;
        if (!options.Enabled || !options.EnableTrackedJobAccess || !options.EnableJobMaintenance) { return new(0, 0, 0, 0, 0); }
        if (!options.IsValid() || !settings.Value.IsValid()) { throw new JobException(JobError.ConfigurationUnavailable); }
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(settings.Value.MaintenanceBudgetSeconds));
        Stopwatch elapsed = Stopwatch.StartNew();
        AgentMaintenanceDomain cursor = await cursors.GetAsync(options.InternalWorkspaceKey, "jobs-v1", forceLoad: true, cancellationToken: budget.Token);
        AgentMaintenance? claim = await cursor.ClaimAsync(options.InternalWorkspaceKey, Guid.NewGuid().ToString("N"), clock.GetUtcNow(), budget.Token);
        if (claim is null) { return new(0, 0, 0, 0, elapsed.ElapsedMilliseconds); }
        int pages = 0, rows = 0, actions = 0, failures = 0;
        try
        {
            while (pages < settings.Value.MaintenanceMaxRowsPerTick && !budget.IsCancellationRequested)
            {
                EntityBag<AgentJob> page = await cursor.PageAsync(claim, budget.Token);
                if (page.Count > 1) { throw new JobException(JobError.ConfigurationUnavailable); }
                pages++;
                foreach (AgentJob snapshot in page)
                {
                    rows++;
                    try
                    {
                        snapshot.RequireValid();
                        if (snapshot.WorkspaceKey != options.InternalWorkspaceKey)
                        { throw new JobException(JobError.JobUnavailable); }
                        AgentJobDomain domain = await jobs.GetAsync(snapshot.WorkspaceShard, snapshot.JobId,
                            forceLoad: true, cancellationToken: budget.Token);
                        AgentJob maintained = await domain.MaintainAsync(clock.GetUtcNow(), settings.Value.PayloadRetentionDays, budget.Token);
                        if (maintained != snapshot) { actions++; }
                        if (options.EnableTrackedJobs && maintained.DispatchStateCode == (int)JobDispatchState.Registered
                            && maintained.ResultStateCode == (int)JobResultState.Pending && maintained.FirstAttemptUtc is null && maintained.Attempts == 0)
                        {
                            AgentJob sent = await dispatch.DispatchRegisteredAsync(maintained, budget.Token);
                            if (sent != maintained) { actions++; }
                        }
                    }
                    catch (OperationCanceledException) when (budget.IsCancellationRequested) { throw; }
                    catch (Exception) { failures++; } // Bad rows or revoked grants cannot starve later rows. Never log private exceptions.
                }
                AgentMaintenance? checkpoint = await cursor.CheckpointAsync(claim, page.ContinuationToken, clock.GetUtcNow(), false, budget.Token);
                if (checkpoint is null) { break; }
                claim = checkpoint;
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
        finally
        {
            // Cancellation leaves the bounded lease to expire; no detached cleanup or work after invocation.
            if (!budget.IsCancellationRequested)
            { _ = await cursor.CheckpointAsync(claim, claim.Cursor, clock.GetUtcNow(), true, budget.Token); }
        }
        return new(pages, rows, actions, failures, elapsed.ElapsedMilliseconds);
    }
}
