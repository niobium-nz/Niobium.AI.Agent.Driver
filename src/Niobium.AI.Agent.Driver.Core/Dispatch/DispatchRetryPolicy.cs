using Niobium.AI.Agent.Driver.Contracts;

namespace Niobium.AI.Agent.Driver.Core.Dispatch;

public sealed record AttemptOutcome(bool Accepted, bool Uncertain, bool Retryable, GatewayError Error);

/// <summary>Owns provider-specific classification. An uncertain attempt cannot be undone by a rejection.</summary>
public static class DispatchRetryPolicy
{
    public static AttemptOutcome Classify(int status) => status switch
    {
        202 => new(true, false, false, default),
        429 => new(false, false, true, GatewayError.RateLimited),
        408 or >= 500 => new(false, true, true, GatewayError.UpstreamOutcomeUnknown),
        401 or 403 or 404 or 409 or >= 300 and < 400 => new(false, false, false, GatewayError.UpstreamConfiguration),
        400 or 422 => new(false, false, false, GatewayError.UpstreamRejected),
        _ => new(false, true, false, GatewayError.UpstreamOutcomeUnknown)
    };

    /// <summary>A retry cancelled before its send cannot change the last observed provider outcome.</summary>
    public static AttemptOutcome OnCancellation(AttemptOutcome previous, bool attemptStarted) =>
        attemptStarted ? new(false, true, true, GatewayError.UpstreamOutcomeUnknown) : previous;

    public static DispatchAgentResult Result(PreparedDispatch request, AttemptOutcome outcome, bool earlierUncertainty)
    {
        if (outcome.Accepted)
        {
            return new(request.TargetAgent, request.HandoffId, DispatchState.Accepted, AcceptedBy: "chatgpt");
        }
        bool uncertain = earlierUncertainty || outcome.Uncertain;
        GatewayError error = uncertain ? GatewayError.UpstreamOutcomeUnknown : outcome.Error;
        RetryAdvice advice = uncertain || outcome.Retryable
            ? RetryAdvice.RetrySameEventOnly : RetryAdvice.OperatorActionRequired;
        return new(request.TargetAgent, request.HandoffId,
            uncertain ? DispatchState.OutcomeUnknown : DispatchState.Rejected, ErrorCode: error, RetryAdvice: advice);
    }
}

public interface IWorkspaceAgentClient
{
    Task<DispatchAgentResult> DispatchAsync(PreparedDispatch request, string credential, CancellationToken cancellationToken);
}
