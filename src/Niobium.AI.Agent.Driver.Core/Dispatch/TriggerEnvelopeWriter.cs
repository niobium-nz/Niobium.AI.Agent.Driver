using System.Text;
using System.Text.Json;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Entities;

namespace Niobium.AI.Agent.Driver.Core.Dispatch;

/// <summary>Validates bounded JSON and preserves numeric tokens without floating-point conversion.</summary>
public static class TriggerEnvelopeWriter
{
    public static DispatchAgentRequest Parse(JsonElement arguments)
    {
        try
        {
            int properties = 0;
            ValidateTree(arguments, 0, ref properties);
            return arguments.Deserialize<DispatchAgentRequest>(GatewayJson.Options)
                ?? throw new GatewayException(GatewayError.InvalidRequest);
        }
        catch (JsonException)
        {
            throw new GatewayException(GatewayError.InvalidRequest);
        }
    }

    public static byte[] ValidateInput(DispatchAgentRequest request, int maxInputBytes)
    {
        if (!IdentifierRules.IsAlias(request.TargetAgent) || !IdentifierRules.IsEventId(request.HandoffId)
            || (request.ConversationKey is not null && !IdentifierRules.IsEventId(request.ConversationKey))
            || request.Input is null || string.IsNullOrWhiteSpace(request.Input.Task)
            || request.Input.Data.ValueKind != JsonValueKind.Object)
        {
            throw new GatewayException(GatewayError.InvalidRequest);
        }
        JsonElement input = JsonSerializer.SerializeToElement(request.Input, GatewayJson.Options);
        int properties = 0;
        ValidateTree(input, 1, ref properties);
        byte[] bytes = CanonicalBytes(input);
        if (bytes.Length > maxInputBytes)
        {
            throw new GatewayException(GatewayError.InputTooLarge);
        }
        return bytes;
    }

    public static PreparedDispatch Prepare(DispatchAgentRequest request, CallerContext caller,
        AgentRegistration registration, AgentGatewayOptions options)
    {
        byte[] input = ValidateInput(request, Math.Min(options.MaxInputBytes, registration.MaxInputBytes));
        if (request.ConversationKey is not null
            && (!options.AllowConversationReuse || !registration.AllowConversationReuse))
        {
            throw new GatewayException(GatewayError.Forbidden);
        }
        using MemoryStream envelope = new();
        using (Utf8JsonWriter writer = new(envelope))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "niobium.agent-handoff.v1");
            writer.WriteString("handoff_id", request.HandoffId);
            writer.WriteString("target_agent", request.TargetAgent);
            writer.WritePropertyName("input");
            writer.WriteRawValue(input);
            writer.WriteEndObject();
        }
        // Different immutable aliases may share a channel but carry different envelope targets.
        string key = StableIdentity.Derive("niobium-agent-dispatch-v1", caller.WorkspaceKey,
            caller.PrincipalKey, request.TargetAgent, registration.ApiChannelId, request.HandoffId);
        using MemoryStream body = new();
        using (Utf8JsonWriter writer = new(body))
        {
            writer.WriteStartObject();
            writer.WriteString("input", Encoding.UTF8.GetString(envelope.ToArray()));
            if (request.ConversationKey is not null)
            {
                writer.WriteString("conversation_key", StableIdentity.Derive("niobium-agent-conversation-v1",
                    caller.WorkspaceKey, caller.PrincipalKey, registration.TargetAgent, request.ConversationKey));
            }
            writer.WriteEndObject();
        }
        if (body.Length > options.MaxProviderBodyBytes)
        {
            throw new GatewayException(GatewayError.InputTooLarge);
        }
        return new(request.TargetAgent, request.HandoffId, registration.ApiChannelId, key, body.ToArray());
    }

    public static byte[] CanonicalBytes(JsonElement input)
    {
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output))
        {
            WriteCanonical(writer, input);
        }
        return output.ToArray();
    }

    private static void ValidateTree(JsonElement value, int depth, ref int properties)
    {
        if (depth > 32)
        {
            throw new GatewayException(GatewayError.InvalidRequest);
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || ++properties > 1024)
                {
                    throw new GatewayException(GatewayError.InvalidRequest);
                }
                ValidateTree(property.Value, depth + 1, ref properties);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                ValidateTree(item, depth + 1, ref properties);
            }
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText());
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}

/// <summary>One frozen request shared by all immediate attempts. Contains no credential.</summary>
public sealed record PreparedDispatch(string TargetAgent, string HandoffId, string ApiChannelId,
    string IdempotencyKey, ReadOnlyMemory<byte> Body);
