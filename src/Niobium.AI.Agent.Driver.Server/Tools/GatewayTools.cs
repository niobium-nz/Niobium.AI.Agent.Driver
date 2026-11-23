using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Dispatch;
using Niobium.AI.Agent.Driver.Core.Flows;
using Niobium.AI.Agent.Driver.Server.Security;

namespace Niobium.AI.Agent.Driver.Server.Tools;

public sealed class GatewayTools(ICallerContextAccessor callers, ListAgentsFlow list, DispatchAgentFlow dispatch,
    IOptions<AgentGatewayOptions> settings)
{
    public async Task<CallToolResult> ListAsync(ToolInvocationContext context, CancellationToken cancellationToken)
    {
        using CancellationTokenSource budget = CreateBudget(cancellationToken);
        try
        {
            CallerContext caller = await callers.GetAsync(context, budget.Token);
            if (context.Arguments is { Count: > 0 }) { throw new GatewayException(GatewayError.InvalidRequest); }
            ListAgentsResult result = await list.ExecuteAsync(caller, budget.Token);
            return ToolResult(result, false);
        }
        catch (GatewayException error) { return Error(error.Code); }
        catch (OperationCanceledException) { return Error(GatewayError.DeadlineExceeded); }
        catch (Exception) { return Error(GatewayError.ConfigurationUnavailable); }
    }

    /// <summary>Convenience entry point for already typed in-process callers; native MCP uses DispatchRawAsync.</summary>
    public Task<CallToolResult> DispatchAsync(ToolInvocationContext context, CancellationToken cancellationToken) =>
        DispatchRawAsync(context, JsonSerializer.SerializeToElement(context.Arguments, GatewayJson.Options), cancellationToken);

    public async Task<CallToolResult> DispatchRawAsync(ToolInvocationContext context, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource budget = CreateBudget(cancellationToken);
        DispatchAgentRequest? request = null;
        bool flowStarted = false;
        try
        {
            CallerContext caller = await callers.GetAsync(context, budget.Token);
            request = TriggerEnvelopeWriter.Parse(arguments);
            _ = TriggerEnvelopeWriter.ValidateInput(request, settings.Value.MaxInputBytes);
            flowStarted = true;
            DispatchAgentResult result = await dispatch.ExecuteAsync(request, caller, budget.Token);
            return ToolResult(result, result.DispatchState != DispatchState.Accepted);
        }
        catch (GatewayException error)
        {
            if (request is null || !IdentifierRules.IsAlias(request.TargetAgent) || !IdentifierRules.IsEventId(request.HandoffId))
            { return Error(error.Code); }
            return ToolResult(new DispatchAgentResult(request.TargetAgent, request.HandoffId,
                DispatchState.NotDispatched, ErrorCode: error.Code, RetryAdvice: RetryAdvice.OperatorActionRequired), true);
        }
        catch (JsonException) { return Error(GatewayError.InvalidRequest); }
        catch (Exception)
        {
            if (request is not null && flowStarted)
            {
                return ToolResult(new DispatchAgentResult(request.TargetAgent, request.HandoffId,
                    DispatchState.OutcomeUnknown, ErrorCode: GatewayError.UpstreamOutcomeUnknown,
                    RetryAdvice: RetryAdvice.RetrySameEventOnly), true);
            }
            return Error(GatewayError.ConfigurationUnavailable);
        }
    }

    private CancellationTokenSource CreateBudget(CancellationToken cancellationToken)
    {
        CancellationTokenSource result = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        result.CancelAfter(TimeSpan.FromSeconds(settings.Value.DispatchTimeoutSeconds));
        return result;
    }

    public static CallToolResult ToolResult<T>(T value, bool isError) => new()
    {
        IsError = isError,
        StructuredContent = JsonSerializer.SerializeToNode(value, GatewayJson.Options),
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, GatewayJson.Options) }]
    };

    private static CallToolResult Error(GatewayError error) => ToolResult(
        new GatewayToolError(error, RetryAdvice.OperatorActionRequired), true);
}
