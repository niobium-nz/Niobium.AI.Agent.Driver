using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Dispatch;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Flows;

namespace Niobium.AI.Agent.Driver.Server.Tests;

internal sealed class GatewayFixture
{
    public AgentGatewayOptions Options { get; } = new()
    {
        Enabled = true, InternalWorkspaceKey = "test-workspace",
        CredentialAliases = new() { ["test-token"] = "synthetic-test-only-token" }
    };
    public CallerContext Caller { get; } = CallerContext.FromValidatedClaims(
        "test-workspace", "test-issuer", "test-tenant", "test-subject", "test-client", true);
    public MemoryRepository<AgentRegistration> Registrations { get; } = new();
    public MemoryRepository<AgentGrant> Grants { get; } = new();
    public RecordingProvider Provider { get; } = new();
    public AgentRegistration Registration => new()
    {
        WorkspaceKey = Caller.WorkspaceKey, TargetAgent = "research-v1", Enabled = true,
        ApiChannelId = "agtch_test001", CredentialAlias = "test-token", DisplayName = "Research",
        Description = "Synthetic test target", MaxInputBytes = 16384
    };
    public AgentGrant Grant => new()
    {
        CallerPartition = AgentGrant.PartitionFor(Caller), TargetAgent = "research-v1", Enabled = true, CanDispatch = true
    };
    public DispatchAgentRequest Request => new()
    {
        TargetAgent = "research-v1", HandoffId = "test-001",
        Input = new() { Task = "Perform a harmless test", Data = JsonSerializer.SerializeToElement(new { }) }
    };
    public AgentRegistrationDomain RegistrationDomain() => new(new(() => Registrations), []);
    public AgentGrantDomain GrantDomain() => new(new(() => Grants), []);
    public IDomainRepository<AgentRegistrationDomain, AgentRegistration> RegistrationDomains() =>
        new GenericDomainRepository<AgentRegistrationDomain, AgentRegistration>(RegistrationDomain, new(() => Registrations));
    public IDomainRepository<AgentGrantDomain, AgentGrant> GrantDomains() =>
        new GenericDomainRepository<AgentGrantDomain, AgentGrant>(GrantDomain, new(() => Grants));
    public DispatchAgentFlow DispatchFlow() => new(RegistrationDomains(), GrantDomains(), Provider,
        Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System);
    public ListAgentsFlow ListFlow() => new(RegistrationDomains(), GrantDomains(),
        Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System);
    public async Task SeedAsync()
    {
        await Registrations.CreateAsync([Registration]);
        await Grants.CreateAsync([Grant]);
    }
}

internal sealed class RecordingProvider : IWorkspaceAgentClient
{
    public List<PreparedDispatch> Calls { get; } = [];
    public Task<DispatchAgentResult> DispatchAsync(PreparedDispatch request, string credential, CancellationToken cancellationToken)
    {
        Calls.Add(request);
        return Task.FromResult(new DispatchAgentResult(request.TargetAgent, request.HandoffId,
            DispatchState.Accepted, AcceptedBy: "chatgpt"));
    }
}

/// <summary>Unit-test repository only; real adapter tests live in Integration.Tests.</summary>
internal sealed class MemoryRepository<T> : IRepository<T> where T : class
{
    private readonly Dictionary<(string, string), T> rows = [];
    public Exception? ReadFailure { get; set; }
    public Action<T>? AfterWrite { get; set; }
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    private static T Clone(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static (string, string) Key(T value) =>
        (EntityMappingHelper.GetField<string>(value, EntityKeyKind.PartitionKey),
         EntityMappingHelper.GetField<string>(value, EntityKeyKind.RowKey));
    private static string? ETag(T value) => (string?)typeof(T).GetProperty("ETag")!.GetValue(value);
    private static void SetETag(T value, string? tag) => typeof(T).GetProperty("ETag")!.SetValue(value, tag);
    public Task<bool> ExistsAsync(string partitionKey, string rowKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(rows.ContainsKey((partitionKey, rowKey)));
    public Task<T?> RetrieveAsync(string partitionKey, string rowKey, IList<string>? fields = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads++;
        if (ReadFailure is not null) { throw ReadFailure; }
        return Task.FromResult(rows.TryGetValue((partitionKey, rowKey), out T? row) ? Clone(row) : null);
    }
    public Task<IEnumerable<T>> CreateAsync(IEnumerable<T> entities, bool replaceIfExist = false,
        DateTimeOffset? expiry = null, CancellationToken cancellationToken = default)
    {
        Assert.IsFalse(replaceIfExist, "Configuration must not force-upsert.");
        T[] values = entities.ToArray();
        foreach (T value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.ContainsKey(Key(value))) { throw new InvalidOperationException("conflict"); }
            Store(value);
        }
        return Task.FromResult<IEnumerable<T>>(values.Select(Clone).ToArray());
    }
    public Task<IEnumerable<T>> UpdateAsync(IEnumerable<T> entities, bool preconditionCheck = true,
        bool mergeIfExists = false, CancellationToken cancellationToken = default)
    {
        Assert.IsTrue(preconditionCheck);
        Assert.IsFalse(mergeIfExists);
        T[] values = entities.ToArray();
        foreach (T value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!rows.TryGetValue(Key(value), out T? existing) || ETag(existing) != ETag(value))
            { throw new InvalidOperationException("precondition"); }
            Store(value);
        }
        // Deliberately omit a fresh response ETag, matching the mapper's known limitation.
        T[] result = values.Select(Clone).ToArray();
        foreach (T value in result) { SetETag(value, null); }
        return Task.FromResult<IEnumerable<T>>(result);
    }
    private void Store(T value)
    {
        T row = Clone(value);
        SetETag(row, Guid.NewGuid().ToString("N"));
        rows[Key(row)] = row;
        Writes++;
        AfterWrite?.Invoke(row);
    }
    public Task DeleteAsync(IEnumerable<T> entities, bool preconditionCheck = true, bool successIfNotExist = true,
        CancellationToken cancellationToken = default)
    {
        foreach (T value in entities) { rows.Remove(Key(value)); }
        return Task.CompletedTask;
    }
    public Task<EntityBag<T>> GetAsync(int limit, string? continuationToken = null, IList<string>? fields = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<EntityBag<T>> GetAsync(string partitionKey, int limit, string? continuationToken = null,
        IList<string>? fields = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<T> GetAsync(IList<string>? fields = null, CancellationToken cancellationToken = default) =>
        Enumerate(null, cancellationToken);
    public IAsyncEnumerable<T> GetAsync(string partitionKey, IList<string>? fields = null,
        CancellationToken cancellationToken = default) => Enumerate(partitionKey, cancellationToken);
    private async IAsyncEnumerable<T> Enumerate(string? partition, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        foreach (T row in rows.Values.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (partition is null || Key(row).Item1 == partition) { yield return Clone(row); }
        }
    }
}
