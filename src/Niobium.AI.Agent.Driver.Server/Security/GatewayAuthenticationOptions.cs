namespace Niobium.AI.Agent.Driver.Server.Security;

/// <summary>Single-tenant Entra v2 delegated access tokens. No model-selected issuer or audience.</summary>
public sealed class GatewayAuthenticationOptions
{
    public string TenantId { get; set; } = "";
    public string Audience { get; set; } = "";
    public string RequiredScope { get; set; } = "AgentGateway.Dispatch";
    public string[] AllowedClientIds { get; set; } = [];
    public string Issuer => "https://login.microsoftonline.com/" + TenantId + "/v2.0";

    public bool IsValid() => Guid.TryParseExact(TenantId, "D", out _)
        && !string.IsNullOrWhiteSpace(Audience) && !Audience.Any(char.IsWhiteSpace)
        && !string.IsNullOrWhiteSpace(RequiredScope) && !RequiredScope.Any(char.IsWhiteSpace)
        && AllowedClientIds.Length > 0
        && AllowedClientIds.All(id => Guid.TryParseExact(id, "D", out _));
}
