using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Flows;
using Niobium.AI.Agent.Driver.Core.Jobs;
using Niobium.AI.Agent.Driver.Server.Security;

namespace Niobium.AI.Agent.Driver.Server.Tools;

public enum JobToolOperation { Start = 0, Submit = 1, Status = 2, Result = 3 }

/// <summary>Native MCP boundary. Disabled tools fail before authentication or persistence access.</summary>
public sealed class AgentJobTools(ICallerContextAccessor callers, AgentJobFlow flow, IOptions<AgentGatewayOptions> settings)
{
    public async Task<CallToolResult> InvokeAsync(JobToolOperation operation, ToolInvocationContext context,
        string? rawBinding, CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled || !settings.Value.EnableTrackedJobAccess
            || (operation == JobToolOperation.Start && !settings.Value.EnableTrackedJobs))
        { return Error(JobError.TrackedJobsDisabled, RetryAdvice.OperatorActionRequired); }
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(settings.Value.DispatchTimeoutSeconds));
        try
        {
            CallerContext caller = await callers.GetAsync(context, budget.Token);
            JsonElement arguments = NativeGatewayFunctions.ReadRawArguments(rawBinding);
            object result = operation switch
            {
                JobToolOperation.Start => await flow.StartAsync(JobWire.Parse<StartAgentJobRequest>(arguments), caller, budget.Token),
                JobToolOperation.Submit => await flow.SubmitAsync(JobWire.Parse<SubmitJobResultRequest>(arguments), caller, budget.Token),
                JobToolOperation.Status => await flow.StatusAsync(JobWire.Parse<GetAgentJobRequest>(arguments), caller, budget.Token),
                JobToolOperation.Result => await flow.ResultAsync(JobWire.Parse<GetAgentJobRequest>(arguments), caller, budget.Token),
                _ => throw new JobException(JobError.InvalidRequest)
            };
            return GatewayTools.ToolResult(result, false);
        }
        catch (JobException error)
        {
            return Error(error.Code, error.Code is JobError.StorageConflict or JobError.ConfigurationUnavailable or JobError.DeadlineExceeded
                ? RetryAdvice.RetrySameEventOnly : RetryAdvice.OperatorActionRequired);
        }
        catch (GatewayException error)
        { return GatewayTools.ToolResult(new GatewayToolError(error.Code, RetryAdvice.OperatorActionRequired), true); }
        catch (OperationCanceledException) { return Error(JobError.DeadlineExceeded, RetryAdvice.RetrySameEventOnly); }
        catch (Exception) { return Error(JobError.ConfigurationUnavailable, RetryAdvice.RetrySameEventOnly); }
    }

    private static CallToolResult Error(JobError error, RetryAdvice advice) => GatewayTools.ToolResult(new JobToolError(error, advice), true);
}
