using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Dispatch;
using Niobium.AI.Agent.Driver.Server.Security;
using Niobium.AI.Agent.Driver.Server.Tools;

namespace Niobium.AI.Agent.Driver.Server.Tests;

[TestClass]
public sealed class GatewayReviewRegressionTests
{
    [TestMethod]
    public void AliasesSharingAChannelHaveSeparateStableEventKeys()
    {
        GatewayFixture fixture = new();
        PreparedDispatch original = TriggerEnvelopeWriter.Prepare(fixture.Request, fixture.Caller, fixture.Registration, fixture.Options);
        var request = fixture.Request with { TargetAgent = "research-v2" };
        var registration = fixture.Registration with { TargetAgent = request.TargetAgent };
        PreparedDispatch other = TriggerEnvelopeWriter.Prepare(request, fixture.Caller, registration, fixture.Options);
        PreparedDispatch retry = TriggerEnvelopeWriter.Prepare(request, fixture.Caller, registration, fixture.Options);
        Assert.AreEqual(original.ApiChannelId, other.ApiChannelId);
        Assert.AreNotEqual(original.IdempotencyKey, other.IdempotencyKey);
        Assert.AreEqual(other.IdempotencyKey, retry.IdempotencyKey);
        CollectionAssert.AreEqual(other.Body.ToArray(), retry.Body.ToArray());
    }

    [TestMethod]
    public async Task AuthenticationCancellationIsADeadlineBeforeAnyDispatch()
    {
        GatewayFixture fixture = new();
        GatewayTools tools = new(new CancelledCaller(), fixture.ListFlow(), fixture.DispatchFlow(), Options.Create(fixture.Options));
        var result = await tools.DispatchAsync(new() { Name = "dispatch_agent" }, default);
        Assert.IsTrue(result.IsError == true);
        Assert.AreEqual("deadline_exceeded", result.StructuredContent!["error_code"]!.GetValue<string>());
        Assert.AreEqual("retry_same_event_only", result.StructuredContent!["retry_advice"]!.GetValue<string>());
        Assert.AreEqual(0, fixture.Provider.Calls.Count);
    }

    [TestMethod]
    [DataRow(429, DispatchState.Rejected, GatewayError.RateLimited)]
    [DataRow(503, DispatchState.OutcomeUnknown, GatewayError.UpstreamOutcomeUnknown)]
    public void UnstartedRetryCancellationPreservesThePriorObservation(int status, DispatchState state, GatewayError error)
    {
        GatewayFixture fixture = new();
        PreparedDispatch request = TriggerEnvelopeWriter.Prepare(fixture.Request, fixture.Caller, fixture.Registration, fixture.Options);
        AttemptOutcome previous = DispatchRetryPolicy.Classify(status);
        AttemptOutcome cancelled = DispatchRetryPolicy.OnCancellation(previous, false);
        Assert.AreEqual(previous, cancelled);
        DispatchAgentResult result = DispatchRetryPolicy.Result(request, cancelled, previous.Uncertain);
        Assert.AreEqual(state, result.DispatchState);
        Assert.AreEqual(error, result.ErrorCode);
        Assert.AreEqual(DispatchState.OutcomeUnknown, DispatchRetryPolicy.Result(request,
            DispatchRetryPolicy.OnCancellation(previous, true), previous.Uncertain).DispatchState);
    }

    [TestMethod]
    public async Task TenantCasingDoesNotChangeIssuerValidationOrGrantIdentity()
    {
        const string tenant = "abcdefab-abcd-abcd-abcd-abcdefabcdef";
        const string client = "22222222-2222-2222-2222-222222222222";
        const string subject = "33333333-3333-3333-3333-333333333333";
        GatewayAuthenticationOptions options = new()
        { TenantId = tenant.ToUpperInvariant(), Audience = "api://gateway-test", AllowedClientIds = [client] };
        Assert.IsTrue(options.IsValid());
        Assert.AreEqual(tenant, options.CanonicalTenantId);
        Assert.AreEqual("https://login.microsoftonline.com/" + tenant + "/v2.0", options.Issuer);
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey key = new(rsa) { KeyId = "synthetic" };
        string token = new JwtSecurityTokenHandler().CreateEncodedJwt(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer, Audience = options.Audience,
            NotBefore = DateTime.UtcNow.AddMinutes(-1), Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new(key, SecurityAlgorithms.RsaSha256),
            Subject = new ClaimsIdentity([new Claim("tid", tenant), new Claim("oid", subject),
                new Claim("azp", client), new Claim("scp", "AgentGateway.Dispatch")])
        });
        ToolInvocationContext invocation = new()
        {
            Name = "dispatch_agent", Transport = new HttpTransport("http-streamable")
            { Headers = new(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + token } }
        };
        McpCallerContextAdapter adapter = new(Options.Create(options), Options.Create(new GatewayFixture().Options), new Discovery(key));
        CallerContext uppercase = await adapter.GetAsync(invocation, default);
        options.TenantId = tenant;
        CallerContext lowercase = await adapter.GetAsync(invocation, default);
        Assert.AreEqual(lowercase, uppercase);
        options.TenantId = "common";
        Assert.AreEqual("", options.CanonicalTenantId);
        Assert.IsFalse(options.IsValid());
    }

    private sealed class CancelledCaller : ICallerContextAccessor
    {
        public Task<CallerContext> GetAsync(ToolInvocationContext invocation, CancellationToken cancellationToken) =>
            Task.FromCanceled<CallerContext>(new CancellationToken(true));
    }

    private sealed class Discovery(SecurityKey key) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
        {
            OpenIdConnectConfiguration metadata = new();
            metadata.SigningKeys.Add(key);
            return Task.FromResult(metadata);
        }
        public void RequestRefresh() { }
    }
}
