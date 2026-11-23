using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Core.Entities;

/// <summary>Operator-provisioned binding to one actual API conversation, never a ChatGPT UI URL.</summary>
public sealed record AgentContinuationRoute
{
    [EntityKey(EntityKeyKind.PartitionKey)]
    public string WorkspaceKey { get; set; } = "";
    [EntityKey(EntityKeyKind.RowKey)]
    public string ContinuationRef { get; set; } = "";
    [EntityKey(EntityKeyKind.ETag)]
    public string? ETag { get; set; }
    public int Version { get; set; } = 1;
    public string WriteId { get; set; } = "";
    public bool Enabled { get; set; }
    public string InitiatorPrincipalKey { get; set; } = "";
    public string ParentTargetAgent { get; set; } = "";
    public string ApiChannelId { get; set; } = "";
    public string CredentialAlias { get; set; } = "";
    public string ProviderConversationKey { get; set; } = "";
    public DateTimeOffset ExpiresUtc { get; set; }

    public bool IsValid(AgentGatewayOptions options) => Version == 1
        && IdentifierRules.IsAlias(WorkspaceKey) && WorkspaceKey == options.InternalWorkspaceKey
        && IdentifierRules.IsAlias(ContinuationRef) && AgentJob.IsPrincipal(InitiatorPrincipalKey)
        && IdentifierRules.IsAlias(ParentTargetAgent) && IdentifierRules.IsChannel(ApiChannelId)
        && IdentifierRules.IsAlias(CredentialAlias) && options.CredentialAliases.ContainsKey(CredentialAlias)
        && IdentifierRules.IsEventId(ProviderConversationKey) && ExpiresUtc != default;

    public bool SameBinding(AgentContinuationRoute other) => Version == other.Version
        && WorkspaceKey == other.WorkspaceKey && ContinuationRef == other.ContinuationRef
        && InitiatorPrincipalKey == other.InitiatorPrincipalKey && ParentTargetAgent == other.ParentTargetAgent
        && ApiChannelId == other.ApiChannelId && CredentialAlias == other.CredentialAlias
        && ProviderConversationKey == other.ProviderConversationKey;

    public AgentJob Bind(AgentJob job) => job with
    {
        Version = 2, CompletionModeCode = (int)JobCompletionMode.NotifyParent,
        ContinuationRef = ContinuationRef, ParentTargetAgent = ParentTargetAgent,
        ParentApiChannelId = ApiChannelId, ParentCredentialAlias = CredentialAlias,
        ParentConversationKey = ProviderConversationKey, ContinuationExpiresUtc = ExpiresUtc
    };

    public bool Matches(AgentJob job) => WorkspaceKey == job.WorkspaceKey
        && ContinuationRef == job.ContinuationRef && InitiatorPrincipalKey == job.InitiatorPrincipalKey
        && ParentTargetAgent == job.ParentTargetAgent && ApiChannelId == job.ParentApiChannelId
        && CredentialAlias == job.ParentCredentialAlias && ProviderConversationKey == job.ParentConversationKey;
}
