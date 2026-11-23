using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Dispatch;
using Niobium.AI.Agent.Driver.Core.Entities;

namespace Niobium.AI.Agent.Driver.Core.Jobs;

/// <summary>Application-owned contracts. Routing resolves from operator-provisioned references, never raw model input.</summary>
public static class JobWire
{
    public const string ResultSchemaId = "agent-result.v1";

    public static T Parse<T>(JsonElement arguments)
    {
        try
        {
            int nodes = 0;
            ValidateTree(arguments, 0, ref nodes);
            return arguments.Deserialize<T>(GatewayJson.Options) ?? throw new JobException(JobError.InvalidRequest);
        }
        catch (JsonException) { throw new JobException(JobError.InvalidRequest); }
    }

    public static string CanonicalRequest(StartAgentJobRequest request, AgentGatewayOptions options)
    {
        if (!Enum.IsDefined(request.CompletionMode) || request.ResultSchema != ResultSchemaId)
        { throw new JobException(JobError.UnsupportedResultSchema); }
        if ((request.CompletionMode == JobCompletionMode.StoreOnly && request.ContinuationRef is not null)
            || (request.CompletionMode == JobCompletionMode.NotifyParent && !IdentifierRules.IsAlias(request.ContinuationRef)))
        { throw new JobException(JobError.InvalidRequest); }
        if (request.CompletionMode == JobCompletionMode.NotifyParent && !options.EnableParentContinuation)
        { throw new JobException(JobError.TrackedJobsDisabled); }
        _ = TriggerEnvelopeWriter.ValidateInput(new()
        {
            TargetAgent = request.TargetAgent, HandoffId = request.HandoffId,
            Input = request.Input, ConversationKey = request.ConversationKey
        }, options.MaxInputBytes);
        return Encoding.UTF8.GetString(TriggerEnvelopeWriter.CanonicalBytes(JsonSerializer.SerializeToElement(request, GatewayJson.Options)));
    }

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static string CanonicalResult(JobOutcome outcome, JsonElement value, int maxBytes)
    {
        int nodes = 0;
        ValidateTree(value, 0, ref nodes);
        if (value.ValueKind != JsonValueKind.Object || !Enum.IsDefined(outcome))
        { throw new JobException(JobError.InvalidRequest); }
        string[] names = value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        if (outcome == JobOutcome.Succeeded)
        {
            if (!names.SequenceEqual(new[] { "data", "summary" })
                || value.GetProperty("data").ValueKind != JsonValueKind.Object
                || !NonblankString(value.GetProperty("summary"), 4096))
            { throw new JobException(JobError.InvalidRequest); }
        }
        else if (!names.SequenceEqual(new[] { "code", "message" })
            || value.GetProperty("code").ValueKind != JsonValueKind.String
            || !IdentifierRules.IsAlias(value.GetProperty("code").GetString())
            || !NonblankString(value.GetProperty("message"), 4096))
        { throw new JobException(JobError.InvalidRequest); }
        byte[] bytes = TriggerEnvelopeWriter.CanonicalBytes(value);
        if (bytes.Length > maxBytes) { throw new JobException(JobError.ResultTooLarge); }
        return Encoding.UTF8.GetString(bytes);
    }

    public static string ResultFingerprint(JobOutcome outcome, string json) => Hash(((int)outcome).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + json);

    public static PreparedDispatch Prepare(AgentJob job, IJobCapabilities capabilities, int maxBodyBytes)
    {
        job.RequireValid();
        if (job.RequestJson is null) { throw new JobException(JobError.JobUnavailable); }
        StartAgentJobRequest request = JsonSerializer.Deserialize<StartAgentJobRequest>(job.RequestJson, GatewayJson.Options)!;
        string envelope = JsonSerializer.Serialize(new
        {
            schema = "niobium.agent-job.v1", job_id = job.JobId, handoff_id = job.HandoffId,
            target_agent = job.TargetAgent, input = request.Input, result_schema = job.ResultSchema,
            completion_capability = capabilities.Issue(job), result_deadline_utc = job.ResultDeadlineUtc
        }, GatewayJson.Options);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { input = envelope, conversation_key = job.ProviderConversationKey }, GatewayJson.Options);
        if (body.Length > maxBodyBytes) { throw new JobException(JobError.InputTooLarge); }
        return new(job.TargetAgent, job.HandoffId, job.ApiChannelId,
            StableIdentity.Derive("niobium-job-dispatch-v1", job.WorkspaceKey, job.JobId), body);
    }

    private static bool NonblankString(JsonElement value, int maxLength) => value.ValueKind == JsonValueKind.String
        && value.GetString() is string text && text.Length <= maxLength && !string.IsNullOrWhiteSpace(text);

    private static void ValidateTree(JsonElement value, int depth, ref int nodes)
    {
        if (++nodes > 8192 || depth > 32) { throw new JobException(JobError.InvalidRequest); }
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || names.Count > 1024) { throw new JobException(JobError.InvalidRequest); }
                ValidateTree(property.Value, depth + 1, ref nodes);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { foreach (JsonElement item in value.EnumerateArray()) { ValidateTree(item, depth + 1, ref nodes); } }
    }
}
