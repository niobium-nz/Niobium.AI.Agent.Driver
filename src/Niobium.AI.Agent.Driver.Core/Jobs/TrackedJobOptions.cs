using Niobium.AI.Agent.Driver.Contracts;

namespace Niobium.AI.Agent.Driver.Core.Jobs;

/// <summary>Dedicated job keys and limits. Identity key rotation requires a separate migration.</summary>
public sealed class TrackedJobOptions
{
    public string IdentityKey { get; set; } = "";
    public string ActiveSigningKeyVersion { get; set; } = "";
    public Dictionary<string, string> SigningKeys { get; set; } = new(StringComparer.Ordinal);
    public int ResultDeadlineMinutes { get; set; } = 1440;
    public int CallbackGraceMinutes { get; set; } = 60;
    public int PayloadRetentionDays { get; set; } = 30;
    public int DispatchLeaseSeconds { get; set; } = 60;
    public int MaxResultBytes { get; set; } = 16384;
    public int MaintenanceMaxRowsPerTick { get; set; } = 16;
    public int MaintenanceBudgetSeconds { get; set; } = 45;

    public bool IsValid() => IsKey(IdentityKey) && IdentifierRules.IsAlias(ActiveSigningKeyVersion)
        && SigningKeys.Count is >= 1 and <= 8 && SigningKeys.ContainsKey(ActiveSigningKeyVersion)
        && SigningKeys.All(pair => IdentifierRules.IsAlias(pair.Key) && IsKey(pair.Value)
            && pair.Value != IdentityKey)
        && ResultDeadlineMinutes is >= 1 and <= 10080 && CallbackGraceMinutes is >= 1 and <= 60
        && PayloadRetentionDays is >= 1 and <= 365 && DispatchLeaseSeconds is >= 60 and <= 300
        && MaxResultBytes is >= 1 and <= 16384 && MaintenanceMaxRowsPerTick is >= 1 and <= 64
        && MaintenanceBudgetSeconds is >= 1 and <= 45;

    public static bool IsKey(string? value)
    {
        if (value is null || value.Length is < 44 or > 88 || value.Any(char.IsWhiteSpace)) { return false; }
        Span<byte> bytes = stackalloc byte[64];
        return Convert.TryFromBase64String(value, bytes, out int written) && written is >= 32 and <= 64;
    }
}

/// <summary>Safe categories only; never attach storage responses, tokens or task data.</summary>
public sealed class JobException(JobError code) : Exception(code.ToString())
{
    public JobError Code { get; } = code;
}

public interface IJobCapabilities
{
    string Issue(Entities.AgentJob job);
    bool Verify(string capability, Entities.AgentJob job, DateTimeOffset now);
}
