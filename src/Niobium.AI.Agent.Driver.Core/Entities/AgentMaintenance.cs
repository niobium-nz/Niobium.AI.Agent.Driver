namespace Niobium.AI.Agent.Driver.Core.Entities;

/// <summary>A leased workspace cursor that advances through all sixteen job partitions and wraps.</summary>
public sealed record AgentMaintenance
{
    [EntityKey(EntityKeyKind.PartitionKey)]
    public string WorkspaceKey { get; set; } = "";
    [EntityKey(EntityKeyKind.RowKey)]
    public string SweepName { get; set; } = "jobs-v1";
    [EntityKey(EntityKeyKind.ETag)]
    public string? ETag { get; set; }
    public int Version { get; set; } = 1;
    public int Shard { get; set; }
    public string? Cursor { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public long Generation { get; set; }
    public string WriteId { get; set; } = "";
}
