using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;

namespace Niobium.AI.Agent.Driver.Server.Security;

public interface ICallerContextAccessor
{
    Task<CallerContext> GetAsync(ToolInvocationContext invocation, CancellationToken cancellationToken);
}

/// <summary>Validates the bearer itself. Unverified X-MS-CLIENT-PRINCIPAL headers are never trusted.</summary>
public sealed class McpCallerContextAdapter(IOptions<GatewayAuthenticationOptions> authentication,
    IOptions<AgentGatewayOptions> gateway,
    IConfigurationManager<OpenIdConnectConfiguration> discovery) : ICallerContextAccessor
{
    public async Task<CallerContext> GetAsync(ToolInvocationContext invocation, CancellationToken cancellationToken)
    {
        if (!gateway.Value.Enabled)
        {
            throw new GatewayException(GatewayError.GatewayDisabled);
        }
        bool httpAvailable = invocation.TryGetHttpTransport(out HttpTransport? transport);
        if (!httpAvailable || transport is null || transport.Type != HttpTransportType.Streamable
            || !transport.Headers.TryGetValue("Authorization", out string? authorization)
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayException(GatewayError.Unauthenticated);
        }
        string token = authorization[7..];
        if (token.Length is 0 or > 16384 || token.Any(char.IsWhiteSpace))
        {
            throw new GatewayException(GatewayError.Unauthenticated);
        }
        GatewayAuthenticationOptions options = authentication.Value;
        if (!options.IsValid())
        {
            throw new GatewayException(GatewayError.ConfigurationUnavailable);
        }
        try
        {
            OpenIdConnectConfiguration metadata = await discovery.GetConfigurationAsync(cancellationToken);
            JwtSecurityTokenHandler handler = new() { MapInboundClaims = false, MaximumTokenSizeInBytes = 16384 };
            ClaimsPrincipal principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = options.Issuer,
                ValidateAudience = true,
                ValidAudience = options.Audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = metadata.SigningKeys,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.FromSeconds(30),
                IncludeTokenOnFailedValidation = false
            }, out _);
            cancellationToken.ThrowIfCancellationRequested();
            string? tenant = Single(principal, "tid");
            string? subject = Single(principal, "oid");
            string? client = Single(principal, "azp");
            string? scope = Single(principal, "scp");
            if (!string.Equals(tenant, options.TenantId, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(subject, "D", out Guid objectId)
                || client is null || !options.AllowedClientIds.Contains(client, StringComparer.OrdinalIgnoreCase)
                || scope is null || !scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(options.RequiredScope, StringComparer.Ordinal))
            {
                throw new GatewayException(GatewayError.Forbidden);
            }
            return CallerContext.FromValidatedClaims(gateway.Value.InternalWorkspaceKey, options.Issuer,
                options.TenantId.ToLowerInvariant(), "oid:" + objectId.ToString("D"), client.ToLowerInvariant(), true);
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            discovery.RequestRefresh();
            throw new GatewayException(GatewayError.Unauthenticated);
        }
        catch (SecurityTokenException)
        {
            throw new GatewayException(GatewayError.Unauthenticated);
        }
        catch (ArgumentException)
        {
            throw new GatewayException(GatewayError.Unauthenticated);
        }
        catch (GatewayException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new GatewayException(GatewayError.ConfigurationUnavailable);
        }
    }

    private static string? Single(ClaimsPrincipal principal, string type)
    {
        string[] values = principal.FindAll(type).Select(c => c.Value).ToArray();
        return values.Length == 1 ? values[0] : null;
    }
}
