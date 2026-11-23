using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Server.Security;

/// <summary>Versioned HS256 JWTs with immutable claims. No raw capability is stored or returned to the initiator.</summary>
public sealed class JobCapabilities(IOptions<TrackedJobOptions> settings) : IJobCapabilities
{
    private const string Issuer = "niobium-agent-gateway";
    private const string Audience = "niobium-agent-job-completion";

    public string Issue(AgentJob job)
    {
        SymmetricSecurityKey key = Key(job.SigningKeyVersion);
        JwtHeader header = new(new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        JwtPayload payload = new()
        {
            ["iss"] = Issuer, ["aud"] = Audience, ["iat"] = job.CreatedUtc.ToUnixTimeSeconds(),
            ["nbf"] = job.CreatedUtc.ToUnixTimeSeconds(), ["exp"] = job.CapabilityExpiresUtc.ToUnixTimeSeconds(),
            ["jti"] = job.CapabilityNonce, ["job_id"] = job.JobId, ["workspace"] = job.WorkspaceKey,
            ["target"] = job.TargetAgent, ["writer"] = job.ResultWriterPrincipalKey,
            ["operation"] = "submit_job_result", ["key_version"] = job.SigningKeyVersion, ["version"] = "1"
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    public bool Verify(string capability, AgentJob job, DateTimeOffset now)
    {
        if (capability is null || capability.Length is 0 or > 4096 || now < job.CreatedUtc || now >= job.CapabilityExpiresUtc)
        { return false; }
        try
        {
            JwtSecurityTokenHandler handler = new() { MapInboundClaims = false, MaximumTokenSizeInBytes = 4096 };
            _ = handler.ValidateToken(capability, new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = Issuer, ValidateAudience = true, ValidAudience = Audience,
                RequireSignedTokens = true, ValidateIssuerSigningKey = true, IssuerSigningKey = Key(job.SigningKeyVersion),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256], ValidateLifetime = true, RequireExpirationTime = true,
                ClockSkew = TimeSpan.Zero, IncludeTokenOnFailedValidation = false,
                LifetimeValidator = (notBefore, expires, _, _) => notBefore is not null && expires is not null
                    && now.UtcDateTime >= notBefore.Value && now.UtcDateTime < expires.Value
            }, out _);
            // Also bind every claim/header to the persisted immutable job, including nonce, key version and operation.
            // Canonical re-issuance prevents algorithm/key substitutions and alternate byte encodings on retries.
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(capability), Encoding.UTF8.GetBytes(Issue(job)));
        }
        catch (SecurityTokenException) { return false; }
        catch (ArgumentException) { return false; }
        catch (JobException) { return false; }
    }

    private SymmetricSecurityKey Key(string version)
    {
        if (!settings.Value.SigningKeys.TryGetValue(version, out string? value) || !TrackedJobOptions.IsKey(value))
        { throw new JobException(JobError.ConfigurationUnavailable); }
        return new SymmetricSecurityKey(Convert.FromBase64String(value)) { KeyId = version };
    }
}
