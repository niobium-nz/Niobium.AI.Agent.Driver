using Niobium.AI.Agent.Driver.Contracts;

namespace Niobium.AI.Agent.Driver.Core;

/// <summary>Stage 1 settings. No job persistence, background execution or result tracking.</summary>
public sealed class AgentGatewayOptions
{
    public bool Enabled { get; set; }
    public string InternalWorkspaceKey { get; set; } = "";
    public int DispatchTimeoutSeconds { get; set; } = 25;
    public int AttemptTimeoutSeconds { get; set; } = 8;
    public int MaxDispatchAttempts { get; set; } = 3;
    public int MaxInputBytes { get; set; } = 16 * 1024;
    public int MaxProviderBodyBytes { get; set; } = 64 * 1024;
    public int MaxRegistryEntries { get; set; } = 256;
    public bool AllowConversationReuse { get; set; }
    public Dictionary<string, string> CredentialAliases { get; set; } = new(StringComparer.Ordinal);

    public bool IsValid()
    {
        bool limitsValid = DispatchTimeoutSeconds is >= 1 and <= 25
            && AttemptTimeoutSeconds >= 1 && AttemptTimeoutSeconds <= DispatchTimeoutSeconds
            && MaxDispatchAttempts is >= 1 and <= 3
            && MaxInputBytes is >= 1 and <= 16384
            && MaxProviderBodyBytes is >= 1024 and <= 65536
            && MaxRegistryEntries is >= 1 and <= 1024;
        if (!limitsValid || !Enabled)
        {
            return limitsValid;
        }

        return IdentifierRules.IsAlias(InternalWorkspaceKey)
            && CredentialAliases.Count > 0
            && CredentialAliases.All(pair => IdentifierRules.IsAlias(pair.Key)
                && !string.IsNullOrWhiteSpace(pair.Value)
                && !pair.Value.StartsWith("@Microsoft.KeyVault(", StringComparison.Ordinal)
                && !pair.Value.Any(char.IsWhiteSpace));
    }

    public void RequireCaller(CallerContext caller)
    {
        if (!Enabled)
        {
            throw new GatewayException(GatewayError.GatewayDisabled);
        }
        if (!IsValid() || caller.WorkspaceKey != InternalWorkspaceKey || !caller.HasDispatchScope)
        {
            throw new GatewayException(GatewayError.Forbidden);
        }
    }
}

/// <summary>Contains only a fixed safe category, not provider bodies or caller data.</summary>
public sealed class GatewayException(GatewayError code) : Exception(code.ToString())
{
    public GatewayError Code { get; } = code;
}

public static class IdentifierRules
{
    public static bool IsAlias(string? value) => value is { Length: >= 1 and <= 64 }
        && IsLowerAlphaNumeric(value[0])
        && value.All(c => IsLowerAlphaNumeric(c) || c == '-');

    public static bool IsEventId(string? value) => value is { Length: >= 1 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-');

    public static bool IsChannel(string? value) => value is { Length: > 6 and <= 160 }
        && value.StartsWith("agtch_", StringComparison.Ordinal)
        && value.AsSpan(6).ToArray().All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static bool IsLowerAlphaNumeric(char value) => value is >= 'a' and <= 'z' or >= '0' and <= '9';
}
