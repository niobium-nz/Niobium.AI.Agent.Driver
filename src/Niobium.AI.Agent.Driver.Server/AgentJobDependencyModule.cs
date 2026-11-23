using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Flows;
using Niobium.AI.Agent.Driver.Core.Jobs;
using Niobium.AI.Agent.Driver.Server.Security;
using Niobium.AI.Agent.Driver.Server.Tools;

namespace Niobium.AI.Agent.Driver.Server;

public static class AgentJobDependencyModule
{
    public static void AddAgentJobs(this FunctionsApplicationBuilder builder)
    {
        builder.Services.AddDomain<AgentJobDomain, AgentJob>();
        builder.Services.AddDomain<AgentMaintenanceDomain, AgentMaintenance>();
        builder.Services.AddOptions<TrackedJobOptions>().BindConfiguration(nameof(TrackedJobOptions))
            .Validate<IOptions<AgentGatewayOptions>>((options, gateway) => !gateway.Value.EnableTrackedJobAccess || options.IsValid(),
                "Tracked job access requires dedicated strong keys and bounded limits.").ValidateOnStart();
        builder.Services.AddSingleton<IJobCapabilities, JobCapabilities>();
        builder.Services.AddTransient<AgentJobFlow>();
        builder.Services.AddTransient<AgentJobMaintenanceFlow>();
        builder.Services.AddTransient<AgentJobTools>();
        builder.ConfigureMcpTool("start_agent_job")
            .WithProperty("target_agent", "string", "Versioned alias with an explicit tracked-job grant.", true)
            .WithProperty("handoff_id", "string", "Stable event ID. Preserve this and the complete request for every retry.", true)
            .WithProperty("input", "object", "Nonblank task and optional data object. Maximum 16 KiB canonical UTF-8.", true)
            .WithProperty("result_schema", "string", "Currently agent-result.v1 only.", true)
            .WithProperty("completion_mode", "string", "Currently store_only only. Parent continuation is not supported.", true)
            .WithProperty("conversation_key", "string", "Optional stable conversation key, only under both reuse policies.");
        builder.ConfigureMcpTool("submit_job_result")
            .WithProperty("job_id", "string", "Opaque job ID from your received envelope.", true)
            .WithProperty("completion_capability", "string", "Secret job-scoped capability from that envelope. Do not disclose or log it.", true)
            .WithProperty("outcome", "string", "succeeded or failed.", true)
            .WithProperty("result", "object", "Success: summary string and data object. Failure: code alias and message string. At most 16 KiB canonical UTF-8.", true);
        builder.ConfigureMcpTool("get_job_status").WithProperty("job_id", "string", "Your opaque job ID.", true);
        builder.ConfigureMcpTool("get_job_result").WithProperty("job_id", "string", "Your opaque job ID.", true);
    }
}
