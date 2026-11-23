using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Dispatch;

namespace Niobium.AI.Agent.Driver.Server.Integration;

public interface IDispatchDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class DispatchDelay : IDispatchDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

/// <summary>One retry owner, fixed origin, frozen body. Never reads or logs provider response bodies.</summary>
public sealed class WorkspaceAgentClient(HttpClient http, IOptions<AgentGatewayOptions> settings,
    IDispatchDelay delay, TimeProvider clock, ILogger<WorkspaceAgentClient> logger) : IWorkspaceAgentClient
{
    public async Task<DispatchAgentResult> DispatchAsync(PreparedDispatch request, string credential,
        CancellationToken cancellationToken)
    {
        AgentGatewayOptions options = settings.Value;
        if (!IdentifierRules.IsChannel(request.ApiChannelId) || string.IsNullOrWhiteSpace(credential)
            || credential.StartsWith("@Microsoft.KeyVault(", StringComparison.Ordinal)
            || credential.Any(char.IsWhiteSpace))
        {
            return new(request.TargetAgent, request.HandoffId, DispatchState.NotDispatched,
                ErrorCode: GatewayError.ConfigurationUnavailable, RetryAdvice: RetryAdvice.OperatorActionRequired);
        }
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(options.DispatchTimeoutSeconds));
        Stopwatch elapsed = Stopwatch.StartNew();
        bool uncertain = false;
        bool sent = false;
        AttemptOutcome outcome = new(false, false, false, GatewayError.DeadlineExceeded);
        int attempts = 0;
        for (int attempt = 1; attempt <= options.MaxDispatchAttempts; attempt++)
        {
            if (budget.IsCancellationRequested)
            {
                break;
            }
            TimeSpan? retryAfter = null;
            using HttpRequestMessage message = new(HttpMethod.Post,
                new Uri("https://api.chatgpt.com/v1/workspace_agents/" + request.ApiChannelId + "/trigger"));
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            message.Headers.Add("Idempotency-Key", request.IdempotencyKey);
            message.Content = new ReadOnlyMemoryContent(request.Body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using CancellationTokenSource attemptDeadline = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            attemptDeadline.CancelAfter(TimeSpan.FromSeconds(options.AttemptTimeoutSeconds));
            try
            {
                attemptDeadline.Token.ThrowIfCancellationRequested();
                sent = true;
                attempts++;
                using HttpResponseMessage response = await http.SendAsync(message,
                    HttpCompletionOption.ResponseHeadersRead, attemptDeadline.Token);
                outcome = DispatchRetryPolicy.Classify((int)response.StatusCode);
                // A known 202 is sufficient. An empty/malformed/slow metadata body cannot cause resubmission.
                if (outcome.Accepted)
                {
                    break;
                }
                retryAfter = response.Headers.RetryAfter?.Delta;
                if (retryAfter is null && response.Headers.RetryAfter?.Date is DateTimeOffset date)
                {
                    retryAfter = date - clock.GetUtcNow();
                }
            }
            catch (OperationCanceledException)
            {
                outcome = new(false, sent, true, GatewayError.UpstreamOutcomeUnknown);
            }
            catch (HttpRequestException)
            {
                outcome = new(false, true, true, GatewayError.UpstreamOutcomeUnknown);
            }
            catch (Exception)
            {
                // Do not let an unexpected post-send error become a false not_dispatched result.
                outcome = new(false, sent, false, GatewayError.UpstreamOutcomeUnknown);
            }
            uncertain |= outcome.Uncertain;
            if (!outcome.Retryable || attempt == options.MaxDispatchAttempts || budget.IsCancellationRequested)
            {
                break;
            }
            TimeSpan pause = retryAfter ?? TimeSpan.FromMilliseconds(Math.Min(2000,
                200 * (1 << (attempt - 1))) + Random.Shared.Next(0, 100));
            pause = pause < TimeSpan.Zero ? TimeSpan.Zero : pause;
            TimeSpan remaining = TimeSpan.FromSeconds(options.DispatchTimeoutSeconds) - elapsed.Elapsed;
            if (pause >= remaining)
            {
                break;
            }
            try
            {
                await delay.WaitAsync(pause, budget.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        DispatchAgentResult result = sent
            ? DispatchRetryPolicy.Result(request, outcome, uncertain)
            : new(request.TargetAgent, request.HandoffId, DispatchState.NotDispatched,
                ErrorCode: GatewayError.DeadlineExceeded, RetryAdvice: RetryAdvice.RetrySameEventOnly);
        // No exception object, prompt, payload, token, raw event ID, channel or response body.
        logger.LogInformation("Gateway dispatch {EventHash} {TargetAlias} {Outcome} {Attempts} {DurationMs}",
            request.IdempotencyKey, request.TargetAgent, result.DispatchState, attempts, elapsed.ElapsedMilliseconds);
        return result;
    }
}
