using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Dispatch;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Server.Integration;

public interface IDispatchDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class DispatchDelay : IDispatchDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

/// <summary>One retry owner, fixed origin and frozen body. Stage 1 does not read response bodies.</summary>
public sealed partial class WorkspaceAgentClient(HttpClient http, IOptions<AgentGatewayOptions> settings,
    IDispatchDelay delay, TimeProvider clock, ILogger<WorkspaceAgentClient> logger) : IWorkspaceAgentRunClient
{
    public async Task<DispatchAgentResult> DispatchAsync(PreparedDispatch request, string credential, CancellationToken cancellationToken) =>
        (await DispatchCoreAsync(request, credential, false, cancellationToken)).Outcome;

    public Task<TrackedDispatchResult> DispatchTrackedAsync(PreparedDispatch request, string credential, CancellationToken cancellationToken) =>
        DispatchCoreAsync(request, credential, true, cancellationToken);

    private async Task<TrackedDispatchResult> DispatchCoreAsync(PreparedDispatch request, string credential,
        bool includeRunMetadata, CancellationToken cancellationToken)
    {
        AgentGatewayOptions options = settings.Value;
        if (!IdentifierRules.IsChannel(request.ApiChannelId) || !IsCredential(credential))
        {
            return new(new DispatchAgentResult(request.TargetAgent, request.HandoffId, DispatchState.NotDispatched,
                ErrorCode: GatewayError.ConfigurationUnavailable, RetryAdvice: RetryAdvice.OperatorActionRequired));
        }
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(options.DispatchTimeoutSeconds));
        Stopwatch elapsed = Stopwatch.StartNew();
        bool uncertain = false, sent = false;
        string? runId = null;
        AttemptOutcome outcome = new(false, false, false, GatewayError.DeadlineExceeded);
        int attempts = 0;
        for (int attempt = 1; attempt <= options.MaxDispatchAttempts; attempt++)
        {
            if (budget.IsCancellationRequested) { break; }
            bool attemptStarted = false;
            TimeSpan? retryAfter = null;
            using HttpRequestMessage message = new(HttpMethod.Post,
                new Uri("https://api.chatgpt.com/v1/workspace_agents/" + request.ApiChannelId + "/trigger"));
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            message.Headers.Add("Idempotency-Key", request.IdempotencyKey);
            if (includeRunMetadata) { message.Headers.Add("OpenAI-Beta", "workspace_agent_runs=v1"); }
            message.Content = new ReadOnlyMemoryContent(request.Body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using CancellationTokenSource attemptDeadline = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            attemptDeadline.CancelAfter(TimeSpan.FromSeconds(options.AttemptTimeoutSeconds));
            try
            {
                attemptDeadline.Token.ThrowIfCancellationRequested();
                attemptStarted = true;
                sent = true;
                attempts++;
                using HttpResponseMessage response = await http.SendAsync(message,
                    HttpCompletionOption.ResponseHeadersRead, attemptDeadline.Token);
                outcome = DispatchRetryPolicy.Classify((int)response.StatusCode);
                if (outcome.Accepted)
                {
                    // Acceptance is final before any optional bounded metadata read. Metadata failure NEVER retries POST.
                    if (includeRunMetadata)
                    {
                        using CancellationTokenSource metadataDeadline = CancellationTokenSource.CreateLinkedTokenSource(attemptDeadline.Token);
                        metadataDeadline.CancelAfter(TimeSpan.FromSeconds(1));
                        try
                        {
                            using JsonDocument metadata = await ReadBoundedJsonAsync(response.Content, metadataDeadline.Token);
                            RequireObject(metadata.RootElement);
                            if (metadata.RootElement.TryGetProperty("agent_trigger_run_id", out JsonElement value)
                                && value.ValueKind == JsonValueKind.String && JobFollowUp.IsRunId(value.GetString()))
                            { runId = value.GetString(); }
                        }
                        catch (Exception)
                        { logger.LogWarning("Gateway accepted without usable run metadata {EventHash}", request.IdempotencyKey); }
                    }
                    break;
                }
                retryAfter = response.Headers.RetryAfter?.Delta;
                if (retryAfter is null && response.Headers.RetryAfter?.Date is DateTimeOffset date)
                { retryAfter = date - clock.GetUtcNow(); }
            }
            catch (OperationCanceledException) { outcome = DispatchRetryPolicy.OnCancellation(outcome, attemptStarted); }
            catch (HttpRequestException) { outcome = new(false, attemptStarted, true, GatewayError.UpstreamOutcomeUnknown); }
            catch (Exception)
            {
                if (!attemptStarted) { break; }
                outcome = new(false, true, false, GatewayError.UpstreamOutcomeUnknown);
            }
            uncertain |= outcome.Uncertain;
            if (!outcome.Retryable || attempt == options.MaxDispatchAttempts || budget.IsCancellationRequested) { break; }
            TimeSpan pause = retryAfter ?? TimeSpan.FromMilliseconds(Math.Min(2000,
                200 * (1 << (attempt - 1))) + Random.Shared.Next(0, 100));
            pause = pause < TimeSpan.Zero ? TimeSpan.Zero : pause;
            TimeSpan remaining = TimeSpan.FromSeconds(options.DispatchTimeoutSeconds) - elapsed.Elapsed;
            if (pause >= remaining) { break; }
            try { await delay.WaitAsync(pause, budget.Token); }
            catch (OperationCanceledException) { break; }
        }
        DispatchAgentResult result = sent
            ? DispatchRetryPolicy.Result(request, outcome, uncertain)
            : new(request.TargetAgent, request.HandoffId, DispatchState.NotDispatched,
                ErrorCode: GatewayError.DeadlineExceeded, RetryAdvice: RetryAdvice.RetrySameEventOnly);
        logger.LogInformation("Gateway dispatch {EventHash} {TargetAlias} {Outcome} {Attempts} {DurationMs}",
            request.IdempotencyKey, request.TargetAgent, result.DispatchState, attempts, elapsed.ElapsedMilliseconds);
        return new(result, runId);
    }

    private static bool IsCredential(string? value) => !string.IsNullOrWhiteSpace(value)
        && !value.StartsWith("@Microsoft.KeyVault(", StringComparison.Ordinal) && !value.Any(char.IsWhiteSpace);
}
