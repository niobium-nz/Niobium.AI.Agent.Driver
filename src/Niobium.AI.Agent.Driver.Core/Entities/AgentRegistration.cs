namespace Niobium.AI.Agent.Driver.Core.Entities;

/// <summary>Configuration only. Rebinding a target requires a new immutable alias.</summary>
public sealed record AgentRegistration
{
    [EntityKey(EntityKeyKind.PartitionKey)]
    public string WorkspaceKey { get; set; } = "";
    [EntityKey(EntityKeyKind.RowKey)]
    public string TargetAgent { get; set; } = "";
    [EntityKey(EntityKeyKind.ETag)]
    public string? ETag { get; set; }
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string ApiChannelId { get; set; } = "";
    public string CredentialAlias { get; set; } = "";
    public bool AllowConversationReuse { get; set; }
    public int MaxInputBytes { get; set; } = 16384;
    public string WriteId { get; set; } = "";
}
