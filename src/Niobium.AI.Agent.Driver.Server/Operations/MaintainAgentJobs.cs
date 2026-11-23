using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Niobium.AI.Agent.Driver.Core.Flows;

namespace Niobium.AI.Agent.Driver.Server.Operations;

/// <summary>Host-disabled by ordinary deployments and independently guarded by application opt-ins.</summary>
public sealed class MaintainAgentJobs(AgentJobMaintenanceFlow flow, ILogger<MaintainAgentJobs> logger)
{
    [Function("MaintainAgentJobs")]
    public async Task RunAsync([TimerTrigger("0 * * * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        try
        {
            JobMaintenanceReport report = await flow.ExecuteAsync(cancellationToken);
            logger.LogInformation("Job maintenance {Pages} {Rows} {Actions} {Failures} {DurationMs}",
                report.Pages, report.Rows, report.Actions, report.Failures, report.DurationMs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            // Avoid exposing SDK exceptions, cursor values or stored private payloads through host logs.
            throw new InvalidOperationException("Job maintenance failed; inspect safe metrics and configuration.");
        }
    }
}
