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
using Niobium.AI.Agent.Driver.Server.Security;

namespace Niobium.AI.Agent.Driver.Server.Tests;

[TestClass]
public sealed class AuthenticationTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";
    private const string Subject = "33333333-3333-3333-3333-333333333333";
    private static GatewayAuthenticationOptions Settings() => new()
    { TenantId = Tenant, Audience = "api://gateway-test", AllowedClientIds = [Client] };

    [TestMethod]
    public async Task ValidSignatureAudienceScopeAndTenantProduceAnOpaquePrincipal()
    {
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey key = new(rsa) { KeyId = "test-key" };
        GatewayAuthenticationOptions settings = Settings();
        McpCallerContextAdapter adapter = Adapter(settings, key);
        CallerContext caller = await adapter.GetAsync(Context(Token(settings, key)), default);
        Assert.AreEqual("test-workspace", caller.WorkspaceKey);
        Assert.IsTrue(caller.HasDispatchScope);
        Assert.MatchesRegex("^[A-Za-z0-9_-]{43}$", caller.PrincipalKey);
        Assert.AreEqual(caller, await adapter.GetAsync(Context(Token(settings, key)), default));
        CallerContext other = await adapter.GetAsync(Context(Token(settings, key, subject: "44444444-4444-4444-4444-444444444444")), default);
        Assert.AreNotEqual(caller.PrincipalKey, other.PrincipalKey);
    }

    [TestMethod]
    public async Task SpoofedPlatformHeadersMissingBearerAndWrongTransportAreRejected()
    {
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey key = new(rsa) { KeyId = "test-key" };
        McpCallerContextAdapter adapter = Adapter(Settings(), key);
        ToolInvocationContext[] contexts =
        [
            new() { Name = "dispatch_agent" },
            new() { Name = "dispatch_agent", Transport = new HttpTransport("http-streamable")
                { Headers = new() { ["X-MS-CLIENT-PRINCIPAL"] = "forged", ["X-MS-CLIENT-PRINCIPAL-ID"] = Subject } } },
            new() { Name = "dispatch_agent", Transport = new HttpTransport("http-sse")
                { Headers = new() { ["Authorization"] = "Bearer " + Token(Settings(), key) } } },
            Context(""), Context("bad token"), Context(new string('x', 16385)), Context("not-a-jwt")
        ];
        foreach (ToolInvocationContext context in contexts)
        {
            GatewayException error = await Assert.ThrowsExactlyAsync<GatewayException>(() => adapter.GetAsync(context, default));
            Assert.AreEqual(GatewayError.Unauthenticated, error.Code);
        }
    }

    [TestMethod]
    [DataRow("audience")] [DataRow("audience-slash")] [DataRow("issuer")] [DataRow("expiry")] [DataRow("signature")]
    public async Task TokenValidationRejectsUntrustedTokens(string mutation)
    {
        using RSA rsa = RSA.Create(2048);
        using RSA other = RSA.Create(2048);
        RsaSecurityKey key = new(rsa) { KeyId = "test-key" };
        RsaSecurityKey otherKey = new(other) { KeyId = "test-key" };
        GatewayAuthenticationOptions settings = Settings();
        string? audience = mutation switch
        {
            "audience" => "wrong-audience",
            "audience-slash" => settings.Audience + "/",
            _ => null
        };
        string token = Token(settings, mutation == "signature" ? otherKey : key,
            audience: audience,
            issuer: mutation == "issuer" ? "https://untrusted.invalid" : null,
            expired: mutation == "expiry");
        GatewayException error = await Assert.ThrowsExactlyAsync<GatewayException>(() => Adapter(settings, key).GetAsync(Context(token), default));
        Assert.AreEqual(GatewayError.Unauthenticated, error.Code);
    }

    [TestMethod]
    [DataRow("tenant")] [DataRow("scope")] [DataRow("client")] [DataRow("subject")] [DataRow("app-only")]
    public async Task SignedButUnauthorizedClaimsAreDenied(string mutation)
    {
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey key = new(rsa) { KeyId = "test-key" };
        GatewayAuthenticationOptions settings = Settings();
        string token = Token(settings, key, tenant: mutation == "tenant" ? Guid.Empty.ToString("D") : null,
            scope: mutation == "scope" ? "Unrelated.Scope" : null,
            client: mutation == "client" ? Guid.Empty.ToString("D") : null,
            subject: mutation == "subject" ? "not-an-object-id" : null,
            appOnly: mutation == "app-only");
        GatewayException error = await Assert.ThrowsExactlyAsync<GatewayException>(() => Adapter(settings, key).GetAsync(Context(token), default));
        Assert.AreEqual(GatewayError.Forbidden, error.Code);
    }

    [TestMethod]
    public async Task DisabledInvalidConfigAndMetadataFailureNeverAuthenticate()
    {
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey key = new(rsa) { KeyId = "test-key" };
        GatewayAuthenticationOptions settings = Settings();
        ToolInvocationContext context = Context(Token(settings, key));
        GatewayFixture fixture = new();
        fixture.Options.Enabled = false;
        Discovery discovery = new(key);
        McpCallerContextAdapter adapter = new(Options.Create(settings), Options.Create(fixture.Options), discovery);
        Assert.AreEqual(GatewayError.GatewayDisabled, (await Assert.ThrowsExactlyAsync<GatewayException>(() => adapter.GetAsync(context, default))).Code);
        fixture.Options.Enabled = true;
        settings.AllowedClientIds = [];
        Assert.AreEqual(GatewayError.ConfigurationUnavailable, (await Assert.ThrowsExactlyAsync<GatewayException>(() => adapter.GetAsync(context, default))).Code);
        settings.AllowedClientIds = [Client];
        discovery.Failure = new IOException("private discovery detail");
        Assert.AreEqual(GatewayError.ConfigurationUnavailable, (await Assert.ThrowsExactlyAsync<GatewayException>(() => adapter.GetAsync(context, default))).Code);
        discovery.Failure = new OperationCanceledException();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => adapter.GetAsync(context, default));
        discovery.Failure = null;
        discovery.Metadata.SigningKeys.Clear();
        await Assert.ThrowsExactlyAsync<GatewayException>(() => adapter.GetAsync(context, default));
        Assert.IsTrue(discovery.Refreshed);
    }

    [TestMethod]
    public void AuthenticationConfigurationIsNarrow()
    {
        GatewayAuthenticationOptions settings = Settings();
        Assert.IsTrue(settings.IsValid());
        Assert.AreEqual("https://login.microsoftonline.com/" + Tenant + "/v2.0", settings.Issuer);
        settings.TenantId = "common"; Assert.IsFalse(settings.IsValid()); settings.TenantId = Tenant;
        settings.Audience = " "; Assert.IsFalse(settings.IsValid()); settings.Audience = "api://test";
        settings.RequiredScope = "two scopes"; Assert.IsFalse(settings.IsValid()); settings.RequiredScope = "one";
        settings.AllowedClientIds = ["not-a-client"]; Assert.IsFalse(settings.IsValid());
    }

    private static McpCallerContextAdapter Adapter(GatewayAuthenticationOptions settings, SecurityKey key) =>
        new(Options.Create(settings), Options.Create(new GatewayFixture().Options), new Discovery(key));
    private static ToolInvocationContext Context(string token) => new()
    {
        Name = "dispatch_agent", Transport = new HttpTransport("http-streamable")
        { Headers = new(StringComparer.OrdinalIgnoreCase) { ["authorization"] = "Bearer " + token } }
    };
    private static string Token(GatewayAuthenticationOptions settings, SecurityKey key, string? audience = null,
        string? issuer = null, bool expired = false, string? tenant = null, string? scope = null,
        string? client = null, string? subject = null, bool appOnly = false)
    {
        Claim permission = appOnly ? new("roles", "Gateway.Health.Read") : new("scp", scope ?? "AgentGateway.Dispatch");
        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = issuer ?? settings.Issuer, Audience = audience ?? settings.Audience,
            NotBefore = DateTime.UtcNow.AddHours(-1), Expires = DateTime.UtcNow.AddMinutes(expired ? -2 : 10),
            SigningCredentials = new(key, SecurityAlgorithms.RsaSha256),
            Subject = new ClaimsIdentity([
                new Claim("tid", tenant ?? Tenant), new Claim("oid", subject ?? Subject),
                new Claim("azp", client ?? Client), permission])
        };
        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }
    private sealed class Discovery(SecurityKey key) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public OpenIdConnectConfiguration Metadata { get; } = Build(key);
        public Exception? Failure { get; set; }
        public bool Refreshed { get; private set; }
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
        {
            if (Failure is not null) { throw Failure; }
            return Task.FromResult(Metadata);
        }
        public void RequestRefresh() => Refreshed = true;
        private static OpenIdConnectConfiguration Build(SecurityKey key)
        {
            OpenIdConnectConfiguration configuration = new();
            configuration.SigningKeys.Add(key);
            return configuration;
        }
    }
}
