using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Dispatch;
using Niobium.AI.Agent.Driver.Core.Jobs;
using Niobium.AI.Agent.Driver.Server.Integration;

namespace Niobium.AI.Agent.Driver.Server.Tests;

[TestClass]
public sealed class WorkspaceAgentObservationTests
{
    private static PreparedDispatch Request => new("research-v1", "event", "agtch_test", "stable-event-key", Encoding.UTF8.GetBytes("{\"input\":\"synthetic\"}"));
    private static WorkspaceAgentClient Client(HttpClient http) => new(http, Options.Create(new AgentGatewayOptions
    { AttemptTimeoutSeconds = 1, MaxProviderBodyBytes = 1024 }), new DispatchDelay(), TimeProvider.System, NullLogger<WorkspaceAgentClient>.Instance);
    private static string Run(string status, string id = "apirun_test", string channel = "agtch_test", string? error = null) =>
        JsonSerializer.Serialize(new { @object = "workspace_agent.trigger_run", id, api_trigger_id = channel, status,
            error = error is null ? null : new { code = error, message = "private provider detail must not escape" } });

    [TestMethod]
    public async Task TrackedDispatchRequestsBetaMetadataButStageOneDoesNot()
    {
        WireHandler handler = new((request, _) =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("https://api.chatgpt.com/v1/workspace_agents/agtch_test/trigger", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            Assert.AreEqual("synthetic-secret", request.Headers.Authorization.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            { Content = new StringContent("{\"agent_trigger_run_id\":\"apirun_test\"}") });
        });
        using HttpClient http = new(handler);
        TrackedDispatchResult tracked = await Client(http).DispatchTrackedAsync(Request, "synthetic-secret", default);
        Assert.AreEqual("workspace_agent_runs=v1", handler.BetaHeaders[0]);
        Assert.AreEqual(DispatchState.Accepted, tracked.Outcome.DispatchState); Assert.AreEqual("apirun_test", tracked.RunId);
        DispatchAgentResult untracked = await Client(http).DispatchAsync(Request, "synthetic-secret", default);
        Assert.AreEqual(DispatchState.Accepted, untracked.DispatchState); Assert.IsNull(handler.BetaHeaders[1]);
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task OptionalMetadataCannotTurnAcceptedIntoRetries()
    {
        foreach (string body in new[] { "", "null", "{", "{}", "[]", "{\"agent_trigger_run_id\":42}",
            "{\"agent_trigger_run_id\":\"../run\"}", "{\"x\":1,\"x\":2}", new string('x', 1100) })
        {
            WireHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(body) }));
            using HttpClient http = new(handler);
            TrackedDispatchResult result = await Client(http).DispatchTrackedAsync(Request, "synthetic-secret", default);
            Assert.AreEqual(DispatchState.Accepted, result.Outcome.DispatchState); Assert.IsNull(result.RunId); Assert.AreEqual(1, handler.Calls);
        }
        WireHandler slow = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
        { Content = new StreamContent(new SlowStream()) }));
        using HttpClient slowHttp = new(slow);
        TrackedDispatchResult accepted = await Client(slowHttp).DispatchTrackedAsync(Request, "synthetic-secret", default);
        Assert.AreEqual(DispatchState.Accepted, accepted.Outcome.DispatchState); Assert.AreEqual(1, slow.Calls);
        WireHandler chunked = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
        { Content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(new string('x', 1100)))) }));
        using HttpClient chunkedHttp = new(chunked);
        Assert.AreEqual(DispatchState.Accepted, (await Client(chunkedHttp).DispatchTrackedAsync(Request, "synthetic-secret", default)).Outcome.DispatchState);
        Assert.AreEqual(1, chunked.Calls);
    }

    [TestMethod]
    public async Task KnownAndUnknownRunStatesAreObservationOnlyWithFixedDestination()
    {
        foreach ((string text, JobProviderState state) in new[]
        {
            ("queued", JobProviderState.Queued), ("in_progress", JobProviderState.InProgress), ("suspended", JobProviderState.Suspended),
            ("completed", JobProviderState.Completed), ("failed", JobProviderState.Failed), ("new_beta_state", JobProviderState.Unrecognized)
        })
        {
            WireHandler handler = new((request, _) =>
            {
                Assert.AreEqual(HttpMethod.Get, request.Method);
                Assert.AreEqual("https://api.chatgpt.com/v1/workspace_agents/agtch_test/runs/apirun_test", request.RequestUri!.AbsoluteUri);
                Assert.AreEqual("synthetic-secret", request.Headers.Authorization!.Parameter);
                Assert.IsFalse(request.Headers.Contains("Idempotency-Key"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Run(text, error: "run_failed")) });
            });
            using HttpClient http = new(handler);
            ProviderObservation observed = await Client(http).ObserveAsync("agtch_test", "apirun_test", "synthetic-secret", default);
            Assert.AreEqual(state, observed.State); Assert.AreEqual(1, handler.Calls);
            Assert.IsFalse(JsonSerializer.Serialize(observed).Contains("private provider", StringComparison.Ordinal));
        }
        foreach ((string code, ProviderObservationError category) in new[]
        { ("dispatch_failed", ProviderObservationError.DispatchFailed), ("future-error", ProviderObservationError.Unavailable) })
        {
            WireHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Run("failed", error: code)) }));
            using HttpClient http = new(handler);
            Assert.AreEqual(category, (await Client(http).ObserveAsync("agtch_test", "apirun_test", "synthetic-secret", default)).Error);
        }
    }

    [TestMethod]
    public async Task WrongRunChannelMalformedAndOversizedResponsesFailWithoutStateChange()
    {
        foreach (string body in new[] { Run("queued", "apirun_other"), Run("queued", channel: "agtch_other"), Run(""), "{}", "null", "{",
            "{\"object\":\"workspace_agent.trigger_run\",\"id\":\"apirun_test\",\"api_trigger_id\":\"agtch_test\",\"status\":\"queued\",\"status\":\"completed\"}", new string('x', 1100) })
        {
            WireHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
            using HttpClient http = new(handler);
            ProviderObservation result = await Client(http).ObserveAsync("agtch_test", "apirun_test", "synthetic-secret", default);
            Assert.IsNull(result.State); Assert.AreEqual(ProviderObservationError.InvalidResponse, result.Error); Assert.AreEqual(1, handler.Calls);
        }
    }

    [TestMethod]
    public async Task ErrorStatusAndRetryAfterAreBoundedWithoutInlineRetries()
    {
        foreach ((HttpStatusCode status, ProviderObservationError error) in new[]
        { (HttpStatusCode.Unauthorized, ProviderObservationError.AccessDenied), (HttpStatusCode.Forbidden, ProviderObservationError.AccessDenied),
          (HttpStatusCode.NotFound, ProviderObservationError.Unavailable), (HttpStatusCode.Redirect, ProviderObservationError.Unavailable),
          (HttpStatusCode.InternalServerError, ProviderObservationError.Unavailable) })
        {
            WireHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(status)));
            using HttpClient http = new(handler);
            Assert.AreEqual(error, (await Client(http).ObserveAsync("agtch_test", "apirun_test", "synthetic-secret", default)).Error);
            Assert.AreEqual(1, handler.Calls);
        }
        foreach (int seconds in new[] { 1, 120, 7200 })
        {
            WireHandler handler = new((_, _) =>
            {
                HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
                return Task.FromResult(response);
            });
            using HttpClient http = new(handler);
            ProviderObservation result = await Client(http).ObserveAsync("agtch_test", "apirun_test", "synthetic-secret", default);
            Assert.AreEqual(ProviderObservationError.RateLimited, result.Error); Assert.AreEqual(Math.Clamp(seconds, 60, 3600), result.RetryAfterSeconds);
        }
    }

    [TestMethod]
    public async Task InvalidInputsDoNotSendAndCancellationIsPropagated()
    {
        WireHandler noSend = new((_, _) => throw new InvalidOperationException("Must not send"));
        using HttpClient http = new(noSend);
        foreach ((string channel, string run, string token) in new[]
        { ("bad", "apirun_test", "secret"), ("agtch_test", "../../secret", "secret"), ("agtch_test", "apirun_test", "@Microsoft.KeyVault(x)"), ("agtch_test", "apirun_test", "bad token") })
        { Assert.AreEqual(ProviderObservationError.Unavailable, (await Client(http).ObserveAsync(channel, run, token, default)).Error); }
        Assert.AreEqual(0, noSend.Calls);
        WireHandler broken = new((_, _) => throw new HttpRequestException("private failure"));
        using HttpClient brokenHttp = new(broken);
        Assert.AreEqual(ProviderObservationError.Unavailable, (await Client(brokenHttp).ObserveAsync("agtch_test", "apirun_test", "secret", default)).Error);
        WireHandler delayed = new(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); });
        using HttpClient delayedHttp = new(delayed);
        Assert.AreEqual(ProviderObservationError.DeadlineExceeded, (await Client(delayedHttp).ObserveAsync("agtch_test", "apirun_test", "secret", default)).Error);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Client(delayedHttp).ObserveAsync("agtch_test", "apirun_test", "secret", cancelled.Token));
    }

    private sealed class WireHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string?> BetaHeaders { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; BetaHeaders.Add(request.Headers.TryGetValues("OpenAI-Beta", out IEnumerable<string>? values) ? values.Single() : null);
            return response(request, token);
        }
    }

    private sealed class SlowStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
}
