using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Dispatch;
using Niobium.AI.Agent.Driver.Server.Integration;

namespace Niobium.AI.Agent.Driver.Server.Tests;

[TestClass]
public sealed class WorkspaceAgentClientTests
{
    [TestMethod]
    [DataRow("")] [DataRow("null")] [DataRow("not-json")] [DataRow("{\"conversation_url\":null}")]
    public async Task AcceptedNeverDependsOnOptionalBody(string body)
    {
        using RecordingHttpHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
        { Content = new StringContent(body) }));
        using HttpClient http = new(handler);
        GatewayFixture fixture = new();
        RecordingDelay delay = new();
        RecordingLogger logger = new();
        WorkspaceAgentClient client = new(http, Options.Create(fixture.Options), delay, TimeProvider.System, logger);
        DispatchAgentResult result = await client.DispatchAsync(Prepare(fixture), "synthetic-test-only-token", default);
        Assert.AreEqual(DispatchState.Accepted, result.DispatchState);
        Assert.AreEqual("chatgpt", result.AcceptedBy);
        Assert.IsNull(result.ErrorCode);
        Assert.AreEqual(1, handler.Bodies.Count);
        Assert.AreEqual(0, delay.Delays.Count);
        Assert.AreEqual("https://api.chatgpt.com/v1/workspace_agents/agtch_test001/trigger", handler.Uris.Single());
        Assert.AreEqual("Bearer synthetic-test-only-token", handler.Authorizations.Single());
        Assert.AreEqual("application/json", handler.ContentTypes.Single());
        string output = string.Join('\n', logger.Messages);
        Assert.IsFalse(output.Contains("synthetic-test-only-token", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains(fixture.Request.Input.Task, StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("agtch_", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AcceptedDoesNotReadAnUnreliableMetadataStream()
    {
        using RecordingHttpHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
        { Content = new ThrowingContent() }));
        using HttpClient http = new(handler);
        GatewayFixture fixture = new();
        Assert.AreEqual(DispatchState.Accepted, (await Client(http, fixture).DispatchAsync(Prepare(fixture), "test-token", default)).DispatchState);
        Assert.AreEqual(1, handler.Bodies.Count);
    }

    [TestMethod]
    [DataRow(400)] [DataRow(401)] [DataRow(403)] [DataRow(404)] [DataRow(409)] [DataRow(422)] [DataRow(302)]
    public async Task DefinitiveRejectionDoesNotBlindlyRetry(int status)
    {
        using RecordingHttpHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using HttpClient http = new(handler);
        GatewayFixture fixture = new();
        DispatchAgentResult result = await Client(http, fixture).DispatchAsync(Prepare(fixture), "test-token", default);
        Assert.AreEqual(DispatchState.Rejected, result.DispatchState);
        Assert.AreEqual(RetryAdvice.OperatorActionRequired, result.RetryAdvice);
        Assert.AreEqual(1, handler.Bodies.Count);
        Assert.IsNull(result.AcceptedBy);
    }

    [TestMethod]
    [DataRow(500, 400, DispatchState.OutcomeUnknown)]
    [DataRow(503, 202, DispatchState.Accepted)]
    [DataRow(408, 403, DispatchState.OutcomeUnknown)]
    public async Task EarlierUncertaintyIsRetainedUnlessAcceptanceIsKnown(int first, int second, DispatchState expected)
    {
        using RecordingHttpHandler handler = new((attempt, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)(attempt == 1 ? first : second))));
        using HttpClient http = new(handler);
        GatewayFixture fixture = new();
        DispatchAgentResult result = await Client(http, fixture).DispatchAsync(Prepare(fixture), "test-token", default);
        Assert.AreEqual(expected, result.DispatchState);
        Assert.AreEqual(2, handler.Bodies.Count);
        CollectionAssert.AreEqual(handler.Bodies[0], handler.Bodies[1]);
        Assert.AreEqual(handler.Keys[0], handler.Keys[1]);
    }

    [TestMethod]
    public async Task RetryAfterIsHonoredAndCannotExceedTheBudget()
    {
        GatewayFixture fixture = new();
        RecordingDelay delay = new();
        using RecordingHttpHandler handler = new((attempt, _) =>
        {
            HttpResponseMessage response = new(attempt == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.Accepted);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(response);
        });
        using HttpClient http = new(handler);
        DispatchAgentResult result = await Client(http, fixture, delay).DispatchAsync(Prepare(fixture), "test-token", default);
        Assert.AreEqual(DispatchState.Accepted, result.DispatchState);
        Assert.AreEqual(TimeSpan.FromSeconds(1), delay.Delays.Single());
        using RecordingHttpHandler longWait = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddDays(1));
            return Task.FromResult(response);
        });
        using HttpClient other = new(longWait);
        result = await Client(other, fixture).DispatchAsync(Prepare(fixture), "test-token", default);
        Assert.AreEqual(DispatchState.Rejected, result.DispatchState);
        Assert.AreEqual(GatewayError.RateLimited, result.ErrorCode);
        Assert.AreEqual(RetryAdvice.RetrySameEventOnly, result.RetryAdvice);
        Assert.AreEqual(1, longWait.Bodies.Count);
    }

    [TestMethod]
    public async Task NetworkErrorsAreBoundedAndNeverExposeExceptionText()
    {
        using RecordingHttpHandler handler = new((_, _) => throw new HttpRequestException("private provider body"));
        using HttpClient http = new(handler);
        GatewayFixture fixture = new();
        RecordingLogger logger = new();
        WorkspaceAgentClient client = new(http, Options.Create(fixture.Options), new RecordingDelay(), TimeProvider.System, logger);
        DispatchAgentResult result = await client.DispatchAsync(Prepare(fixture), "test-token", default);
        Assert.AreEqual(DispatchState.OutcomeUnknown, result.DispatchState);
        Assert.AreEqual(3, handler.Bodies.Count);
        Assert.AreEqual(1, handler.Keys.Distinct().Count());
        Assert.IsFalse(string.Join('\n', logger.Messages).Contains("private provider", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CancellationBeforeSendDiffersFromCancellationAfterSend()
    {
        using RecordingHttpHandler handler = new((_, _) => throw new OperationCanceledException());
        using HttpClient http = new(handler);
        GatewayFixture fixture = new();
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        DispatchAgentResult before = await Client(http, fixture).DispatchAsync(Prepare(fixture), "test-token", canceled.Token);
        Assert.AreEqual(DispatchState.NotDispatched, before.DispatchState);
        Assert.AreEqual(0, handler.Bodies.Count);
        fixture.Options.MaxDispatchAttempts = 1;
        DispatchAgentResult after = await Client(http, fixture).DispatchAsync(Prepare(fixture), "test-token", default);
        Assert.AreEqual(DispatchState.OutcomeUnknown, after.DispatchState);
        Assert.AreEqual(1, handler.Bodies.Count);
    }

    [TestMethod]
    public async Task CancellationDuringBackoffPreservesPriorOutcome()
    {
        using RecordingHttpHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using HttpClient http = new(handler);
        GatewayFixture fixture = new();
        RecordingDelay delay = new() { Cancel = true };
        DispatchAgentResult result = await Client(http, fixture, delay).DispatchAsync(Prepare(fixture), "test-token", default);
        Assert.AreEqual(DispatchState.OutcomeUnknown, result.DispatchState);
        Assert.AreEqual(1, handler.Bodies.Count);
        await new DispatchDelay().WaitAsync(TimeSpan.Zero, default);
    }

    [TestMethod]
    public async Task UnexpectedSuccessStatusAndPostSendErrorsAreConservative()
    {
        GatewayFixture fixture = new();
        foreach (bool throwError in new[] { false, true })
        {
            using RecordingHttpHandler handler = new((_, _) => throwError
                ? throw new InvalidOperationException("private error")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
            using HttpClient http = new(handler);
            DispatchAgentResult result = await Client(http, fixture).DispatchAsync(Prepare(fixture), "test-token", default);
            Assert.AreEqual(DispatchState.OutcomeUnknown, result.DispatchState);
            Assert.AreEqual(1, handler.Bodies.Count);
        }
    }

    [TestMethod]
    public async Task InvalidSecretsAndRoutesDoNotSend()
    {
        using RecordingHttpHandler handler = new((_, _) => throw new AssertFailedException("Must not send"));
        using HttpClient http = new(handler);
        GatewayFixture fixture = new();
        foreach (string token in new[] { "", " ", "bad\r\nheader", "@Microsoft.KeyVault(unresolved)" })
        {
            Assert.AreEqual(DispatchState.NotDispatched, (await Client(http, fixture).DispatchAsync(Prepare(fixture), token, default)).DispatchState);
        }
        Assert.AreEqual(DispatchState.NotDispatched, (await Client(http, fixture).DispatchAsync(
            Prepare(fixture) with { ApiChannelId = "../escape" }, "test-token", default)).DispatchState);
        Assert.AreEqual(0, handler.Bodies.Count);
    }

    private static PreparedDispatch Prepare(GatewayFixture fixture) => TriggerEnvelopeWriter.Prepare(fixture.Request, fixture.Caller, fixture.Registration, fixture.Options);
    private static WorkspaceAgentClient Client(HttpClient http, GatewayFixture fixture, RecordingDelay? delay = null) =>
        new(http, Options.Create(fixture.Options), delay ?? new(), TimeProvider.System, new RecordingLogger());
    private sealed class RecordingDelay : IDispatchDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public bool Cancel { get; init; }
        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            if (Cancel) { throw new OperationCanceledException(); }
            return Task.CompletedTask;
        }
    }
    private sealed class RecordingHttpHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public List<byte[]> Bodies { get; } = [];
        public List<string> Keys { get; } = [];
        public List<string> Uris { get; } = [];
        public List<string> Authorizations { get; } = [];
        public List<string?> ContentTypes { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            Keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
            Uris.Add(request.RequestUri!.AbsoluteUri);
            Authorizations.Add(request.Headers.Authorization!.ToString());
            ContentTypes.Add(request.Content.Headers.ContentType?.MediaType);
            return await response(Bodies.Count, cancellationToken);
        }
    }
    private sealed class RecordingLogger : ILogger<WorkspaceAgentClient>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.IsNull(exception);
            Messages.Add(formatter(state, exception));
        }
    }
    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new IOException("metadata stream failure");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
