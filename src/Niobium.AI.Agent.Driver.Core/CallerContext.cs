using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Niobium.AI.Agent.Driver.Core;

/// <summary>Construct only after token validation. A model-supplied agent name is not an identity.</summary>
public sealed record CallerContext(string WorkspaceKey, string PrincipalKey, bool HasDispatchScope)
{
    public static CallerContext FromValidatedClaims(string workspace, string issuer, string tenant,
        string subject, string authorizedClient, bool hasDispatchScope) =>
        new(workspace, StableIdentity.Derive("niobium-principal-v1", issuer, tenant, subject, authorizedClient), hasDispatchScope);
}

/// <summary>Versioned, length-prefixed SHA-256 identities, not credentials or authorization proofs.</summary>
public static class StableIdentity
{
    public static string Derive(params string[] components)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (string component in components)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(component);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToBase64String(hash.GetHashAndReset()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
