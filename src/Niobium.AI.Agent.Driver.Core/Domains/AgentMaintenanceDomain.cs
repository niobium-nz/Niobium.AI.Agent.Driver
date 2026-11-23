using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Core.Domains;

/// <summary>Owns the sweep lease and one-row cursor checkpoints. No candidate can adopt another worker's ETag.</summary>
public sealed class AgentMaintenanceDomain(Lazy<IRepository<AgentMaintenance>> repository,
    IEnumerable<IDomainEventHandler<IDomain<AgentMaintenance>>> handlers,
    Lazy<IRepository<AgentJob>> jobs) : GenericDomain<AgentMaintenance>(repository, handlers)
{
    public async Task<AgentMaintenance?> ClaimAsync(string workspace, string owner, DateTimeOffset now, CancellationToken token)
    {
        CheckInitialized();
        if (!IdentifierRules.IsAlias(workspace) || workspace != PartitionKey || RowKey != "jobs-v1"
            || !Guid.TryParseExact(owner, "N", out _)) { throw new JobException(JobError.InvalidRequest); }
        for (int attempt = 0; attempt < 4; attempt++)
        {
            AgentMaintenance? current = await ReadAsync(token);
            if (current?.LeaseOwner is not null && current.LeaseUntilUtc > now) { return null; }
            AgentMaintenance next = (current ?? new() { WorkspaceKey = workspace }) with
            { LeaseOwner = owner, LeaseUntilUtc = now.AddSeconds(90), Generation = checked((current?.Generation ?? 0) + 1) };
            AgentMaintenance? saved = await SaveConditionalAsync(next, current is null, token);
            if (saved is not null) { return saved; }
        }
        throw new JobException(JobError.StorageConflict);
    }

    public Task<EntityBag<AgentJob>> PageAsync(AgentMaintenance claim, CancellationToken token)
    {
        Validate(claim);
        if (claim.WorkspaceKey != PartitionKey) { throw new JobException(JobError.InvalidRequest); }
        return jobs.Value.GetAsync(claim.WorkspaceKey + ":" + claim.Shard.ToString("x", System.Globalization.CultureInfo.InvariantCulture),
            1, claim.Cursor, cancellationToken: token);
    }

    public async Task<AgentMaintenance?> CheckpointAsync(AgentMaintenance claim, string? nextCursor, DateTimeOffset now,
        bool release, CancellationToken token)
    {
        AgentMaintenance? current = await ReadAsync(token);
        if (current is null || current.LeaseOwner != claim.LeaseOwner || current.Generation != claim.Generation
            || current.LeaseUntilUtc is null || current.LeaseUntilUtc <= now || claim.WorkspaceKey != current.WorkspaceKey)
        { return null; }
        AgentMaintenance next = release
            ? current with { LeaseOwner = null, LeaseUntilUtc = null }
            : current with { Cursor = nextCursor, Shard = nextCursor is null ? (current.Shard + 1) % 16 : current.Shard };
        return await SaveConditionalAsync(next, false, token);
    }

    private async Task<AgentMaintenance?> ReadAsync(CancellationToken token)
    {
        AgentMaintenance? current = await Repository.RetrieveAsync(PartitionKey, RowKey, cancellationToken: token);
        if (current is not null)
        {
            Validate(current);
            if (current.WorkspaceKey != PartitionKey || current.SweepName != RowKey || string.IsNullOrEmpty(current.ETag))
            { throw new JobException(JobError.ConfigurationUnavailable); }
        }
        return current;
    }

    private static void Validate(AgentMaintenance row)
    {
        if (row.Version != 1 || !IdentifierRules.IsAlias(row.WorkspaceKey) || row.SweepName != "jobs-v1"
            || row.Shard is < 0 or > 15 || row.Generation < 0 || row.Cursor?.Length > 24000
            || (row.LeaseOwner is not null && !Guid.TryParseExact(row.LeaseOwner, "N", out _)))
        { throw new JobException(JobError.ConfigurationUnavailable); }
    }

    private async Task<AgentMaintenance?> SaveConditionalAsync(AgentMaintenance next, bool create, CancellationToken token)
    {
        Validate(next);
        if (!create && (string.IsNullOrEmpty(next.ETag) || next.ETag == "*")) { throw new JobException(JobError.StorageConflict); }
        AgentMaintenance intended = next with { WriteId = Guid.NewGuid().ToString("N") };
        try
        {
            if (create) { _ = await Repository.CreateAsync([intended with { }], cancellationToken: token); }
            else { _ = await Repository.UpdateAsync([intended with { }], cancellationToken: token); }
        }
        catch (Exception) when (!token.IsCancellationRequested) { }
        AgentMaintenance? observed = await ReadAsync(token);
        return observed is not null && (observed with { ETag = null }) == (intended with { ETag = null }) ? observed : null;
    }
}
