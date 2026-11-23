using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Core.Entities;

/// <summary>A conditional, scalar-only Table snapshot. Sensitive fields never belong in public results.</summary>
public sealed record AgentJob
{
    [EntityKey(EntityKeyKind.PartitionKey)]
    public string WorkspaceShard { get; set; } = "";
    [EntityKey(EntityKeyKind.RowKey)]
    public string JobId { get; set; } = "";
    [EntityKey(EntityKeyKind.ETag)]
    public string? ETag { get; set; }
    public int Version { get; set; } = 1;
    public string WriteId { get; set; } = "";
    public string WorkspaceKey { get; set; } = "";
    public string InitiatorPrincipalKey { get; set; } = "";
    public string TargetAgent { get; set; } = "";
    public string HandoffId { get; set; } = "";
    public string RequestFingerprint { get; set; } = "";
    public string? RequestJson { get; set; }
    public string ApiChannelId { get; set; } = "";
    public string CredentialAlias { get; set; } = "";
    public string? ProviderConversationKey { get; set; }
    public string ResultSchema { get; set; } = "";
    public string ResultWriterPrincipalKey { get; set; } = "";
    public string SigningKeyVersion { get; set; } = "";
    public string CapabilityNonce { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ResultDeadlineUtc { get; set; }
    public DateTimeOffset CapabilityExpiresUtc { get; set; }
    public int DispatchStateCode { get; set; }
    public int ResultStateCode { get; set; }
    public int ProviderStateCode { get; set; }
    public int ContinuationStateCode { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? FirstAttemptUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public long ClaimGeneration { get; set; }
    public int? LastErrorCode { get; set; }
    public int? OutcomeCode { get; set; }
    public string? ResultJson { get; set; }
    public string? ResultHash { get; set; }
    public DateTimeOffset? ResultReceivedUtc { get; set; }
    public bool LateArrival { get; set; }
    public DateTimeOffset? PayloadPurgeAfterUtc { get; set; }

    public static string Identity(CallerContext caller, string target, string handoff, string key)
    {
        if (!TrackedJobOptions.IsKey(key)) { throw new JobException(JobError.ConfigurationUnavailable); }
        byte[] preimage = Encoding.UTF8.GetBytes(StableIdentity.Derive("niobium-job-identity-v1",
            caller.WorkspaceKey, caller.PrincipalKey, target, handoff));
        return "job_v1_" + Convert.ToHexString(HMACSHA256.HashData(Convert.FromBase64String(key), preimage)).ToLowerInvariant();
    }

    public static bool IsJobId(string? id) => id is { Length: 71 }
        && id.StartsWith("job_v1_", StringComparison.Ordinal)
        && id.AsSpan(7).ToArray().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string PartitionFor(string workspace, string jobId)
    {
        if (!IdentifierRules.IsAlias(workspace) || !IsJobId(jobId)) { throw new JobException(JobError.InvalidRequest); }
        return workspace + ":" + jobId[7];
    }

    public static bool IsPrincipal(string? value) => value is { Length: 43 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public AgentJobStatus Status() => new(JobId, TargetAgent, HandoffId,
        (JobDispatchState)DispatchStateCode, (JobResultState)ResultStateCode, ResultSchema, CreatedUtc, ResultDeadlineUtc);

    public AgentJobResult Result() => new(JobId, (JobResultState)ResultStateCode, ResultSchema,
        OutcomeCode is int code ? (JobOutcome)code : null,
        ResultStateCode == (int)JobResultState.Available && ResultJson is not null
            ? JsonSerializer.Deserialize<JsonElement>(ResultJson) : null,
        ResultHash, ResultReceivedUtc, LateArrival);

    public void RequireValid()
    {
        if (Version != 1 || !IsJobId(JobId) || !IdentifierRules.IsAlias(WorkspaceKey)
            || WorkspaceShard != PartitionFor(WorkspaceKey, JobId) || !IsPrincipal(InitiatorPrincipalKey)
            || !IdentifierRules.IsAlias(TargetAgent) || !IdentifierRules.IsEventId(HandoffId)
            || !IdentifierRules.IsChannel(ApiChannelId) || !IdentifierRules.IsAlias(CredentialAlias)
            || !IsPrincipal(ResultWriterPrincipalKey) || ResultSchema != JobWire.ResultSchemaId
            || !IdentifierRules.IsAlias(SigningKeyVersion) || CapabilityNonce.Length != 32
            || RequestFingerprint.Length != 64 || CreatedUtc == default || ResultDeadlineUtc <= CreatedUtc
            || CapabilityExpiresUtc <= ResultDeadlineUtc || !Enum.IsDefined((JobDispatchState)DispatchStateCode)
            || !Enum.IsDefined((JobResultState)ResultStateCode) || ProviderStateCode != 0 || ContinuationStateCode != 0
            || (OutcomeCode is int outcome && !Enum.IsDefined((JobOutcome)outcome))
            || Attempts < 0 || ClaimGeneration < 0
            || (ResultStateCode != (int)JobResultState.Purged && RequestJson is null)
            || (ResultStateCode == (int)JobResultState.Available
                && (ResultJson is null || ResultHash is null || OutcomeCode is null || ResultReceivedUtc is null)))
        { throw new JobException(JobError.JobUnavailable); }
        // Conservative limits below Table's physical per-property and aggregate limits.
        JsonElement snapshot = JsonSerializer.SerializeToElement(this);
        foreach (JsonProperty property in snapshot.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && Encoding.Unicode.GetByteCount(property.Value.GetString()!) > 48 * 1024)
            { throw new JobException(JobError.InputTooLarge); }
        }
        if (JsonSerializer.SerializeToUtf8Bytes(this).Length > 256 * 1024)
        { throw new JobException(JobError.InputTooLarge); }
    }
}
