namespace Niobium.AI.Agent.Driver.Core.Entities;

/// <summary>A current grant to a validated principal/connection, never to a self-reported agent.</summary>
public sealed record AgentGrant
{
    [EntityKey(EntityKeyKind.PartitionKey)]
    public string CallerPartition { get; set; } = "";
    [EntityKey(EntityKeyKind.RowKey)]
    public string TargetAgent { get; set; } = "";
    [EntityKey(EntityKeyKind.ETag)]
    public string? ETag { get; set; }
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public bool CanDispatch { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public string WriteId { get; set; } = "";

    public static string PartitionFor(CallerContext caller) => caller.WorkspaceKey + "." + caller.PrincipalKey;
}
