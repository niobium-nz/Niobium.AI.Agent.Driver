using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using ModelContextProtocol.Protocol;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;

namespace Niobium.AI.Agent.Driver.Server.Tools;

/// <summary>Native MCP endpoints. Identity uses the typed transport; dispatch data uses the original JSON binding.</summary>
public sealed class NativeGatewayFunctions(GatewayTools tools)
{
    [Function("ListAgents")]
    public Task<CallToolResult> ListAsync(
        [McpToolTrigger("list_agents", "List enabled target aliases permitted to this authenticated connection. Does not dispatch work.")]
        ToolInvocationContext context, CancellationToken cancellationToken) => tools.ListAsync(context, cancellationToken);

    [Function("DispatchAgent")]
    public Task<CallToolResult> DispatchAsync(
        [McpToolTrigger("dispatch_agent", "Starts a permitted published Workspace Agent and may incur charges or cause external actions. Await submission acknowledgment only, not completion. Reuse the same handoff_id and identical input for a retry; never create a new ID to fix an unknown outcome.")]
        ToolInvocationContext context, FunctionContext functionContext, CancellationToken cancellationToken)
    {
        try
        {
            // The extension's Dictionary<string, object> converter coerces date/Guid strings,
            // case-colliding keys and numbers. Never reconstruct the task from that dictionary.
            if (!functionContext.BindingContext.BindingData.TryGetValue(nameof(context), out object? raw))
            { throw new GatewayException(GatewayError.ConfigurationUnavailable); }
            JsonElement arguments = ReadRawArguments(raw?.ToString());
            return tools.DispatchRawAsync(context, arguments, cancellationToken);
        }
        catch (GatewayException error)
        {
            return Task.FromResult(GatewayTools.ToolResult(new GatewayToolError(error.Code,
                RetryAdvice.OperatorActionRequired), true));
        }
    }

    public static JsonElement ReadRawArguments(string? raw)
    {
        if (raw is null) { throw new GatewayException(GatewayError.ConfigurationUnavailable); }
        if (Encoding.UTF8.GetByteCount(raw) > 128 * 1024)
        { throw new GatewayException(GatewayError.InputTooLarge); }
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 40 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("arguments", out JsonElement arguments))
            { throw new GatewayException(GatewayError.InvalidRequest); }
            return arguments.Clone();
        }
        catch (JsonException) { throw new GatewayException(GatewayError.InvalidRequest); }
    }
}
