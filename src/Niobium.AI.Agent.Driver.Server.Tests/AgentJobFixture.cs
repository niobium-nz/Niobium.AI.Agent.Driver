using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Dispatch;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Flows;
using Niobium.AI.Agent.Driver.Core.Jobs;
using Niobium.AI.Agent.Driver.Server.Security;

namespace Niobium.AI.Agent.Driver.Server.Tests;

internal sealed class AgentJobFixture
{
    public GatewayFixture Legacy { get; } = new();
    public CallerContext Owner => Legacy.Caller;
    public CallerContext Writer => CallerContext.FromValidatedClaims(Owner.WorkspaceKey, "test-issuer", "test-tenant", "writer", "client-b", true);
    public AgentGatewayOptions Gateway => Legacy.Options;
    public TrackedJobOptions Settings { get; } = new()
    {
        // Synthetic keys are confined to test assemblies.
        IdentityKey = Convert.ToBase64String(Enumerable.Repeat((byte)17, 32).ToArray()),
        ActiveSigningKeyVersion = "key-v1",
        SigningKeys = new() { ["key-v1"] = Convert.ToBase64String(Enumerable.Repeat((byte)34, 32).ToArray()) }
    };
    public JobTestRepository<AgentJob> Jobs { get; } = new();
    public JobTestRepository<AgentMaintenance> Cursors { get; } = new();
    public JobTestProvider Provider { get; } = new();
    public JobClock Clock { get; } = new();
    public IJobCapabilities Capabilities => new JobCapabilities(Options.Create(Settings));
    public AgentRegistration Registration => Legacy.Registration with
    { Version = 2, ResultSchema = JobWire.ResultSchemaId, ResultWriterPrincipalKey = Writer.PrincipalKey };
    public StartAgentJobRequest Request => new()
    {
        TargetAgent = "research-v1", HandoffId = "job-test-001", Input = Legacy.Request.Input,
        ResultSchema = JobWire.ResultSchemaId, CompletionMode = JobCompletionMode.StoreOnly
    };
    public IDomainRepository<AgentJobDomain, AgentJob> Domains => new GenericDomainRepository<AgentJobDomain, AgentJob>(
        () => new(new(() => Jobs), []), new(() => Jobs));
    public IDomainRepository<AgentMaintenanceDomain, AgentMaintenance> MaintenanceDomains => new GenericDomainRepository<AgentMaintenanceDomain, AgentMaintenance>(
        () => new(new(() => Cursors), [], new(() => Jobs)), new(() => Cursors));
    public AgentJobFlow Flow => new(Legacy.RegistrationDomains(), Legacy.GrantDomains(), Domains, Provider, Capabilities,
        Options.Create(Gateway), Options.Create(Settings), Clock);
    public AgentJobMaintenanceFlow Maintenance => new(MaintenanceDomains, Domains, Flow, Options.Create(Gateway), Options.Create(Settings), Clock);

    public AgentJobFixture()
    {
        Gateway.EnableTrackedJobs = true;
        Gateway.EnableTrackedJobAccess = true;
        Gateway.EnableJobMaintenance = true;
    }

    public async Task SeedAsync()
    {
        await Legacy.Registrations.CreateAsync([Registration]);
        await Legacy.Grants.CreateAsync([
            Legacy.Grant with { Version = 2, CanStartJob = true },
            new() { Version = 2, CallerPartition = AgentGrant.PartitionFor(Writer), TargetAgent = "research-v1", Enabled = true, CanSubmitResult = true }
        ]);
    }

    public AgentJob Intent(string? handoff = null)
    {
        StartAgentJobRequest request = Request with { HandoffId = handoff ?? Request.HandoffId };
        string id = AgentJob.Identity(Owner, request.TargetAgent, request.HandoffId, Settings.IdentityKey);
        string json = JobWire.CanonicalRequest(request, Gateway);
        return new()
        {
            WorkspaceShard = AgentJob.PartitionFor(Owner.WorkspaceKey, id), JobId = id, WorkspaceKey = Owner.WorkspaceKey,
            InitiatorPrincipalKey = Owner.PrincipalKey, TargetAgent = request.TargetAgent, HandoffId = request.HandoffId,
            RequestJson = json, RequestFingerprint = JobWire.Hash(json), ApiChannelId = Registration.ApiChannelId,
            CredentialAlias = Registration.CredentialAlias, ResultSchema = JobWire.ResultSchemaId,
            ResultWriterPrincipalKey = Writer.PrincipalKey, SigningKeyVersion = Settings.ActiveSigningKeyVersion,
            CapabilityNonce = Guid.NewGuid().ToString("N"), CreatedUtc = Clock.Now,
            ResultDeadlineUtc = Clock.Now.AddMinutes(Settings.ResultDeadlineMinutes),
            CapabilityExpiresUtc = Clock.Now.AddMinutes(Settings.ResultDeadlineMinutes + Settings.CallbackGraceMinutes)
        };
    }

    public AgentJobDomain Domain(string id)
    {
        AgentJobDomain domain = new(new(() => Jobs), []);
        domain.Initialize(AgentJob.PartitionFor(Owner.WorkspaceKey, id), id);
        return domain;
    }

    public Task<AgentJob> RegisterAsync(string? handoff = null)
    {
        AgentJob intended = Intent(handoff);
        return Domain(intended.JobId).RegisterAsync(intended, default);
    }

    public SubmitJobResultRequest Completion(AgentJob job, JobOutcome outcome = JobOutcome.Succeeded) => new()
    {
        JobId = job.JobId, CompletionCapability = Capabilities.Issue(job), Outcome = outcome,
        Result = JsonSerializer.SerializeToElement(outcome == JobOutcome.Succeeded
            ? new Dictionary<string, object> { ["summary"] = "Synthetic result", ["data"] = new { value = 42 } }
            : new Dictionary<string, object> { ["code"] = "unsupported", ["message"] = "Safe synthetic business failure" })
    };

    public async Task<AgentJob> ReadAsync(string id) => (await Jobs.RetrieveAsync(AgentJob.PartitionFor(Owner.WorkspaceKey, id), id))!;
}

internal sealed class JobClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class JobTestProvider : IWorkspaceAgentClient
{
    public List<PreparedDispatch> Calls { get; } = [];
    public Func<PreparedDispatch, Task>? OnDispatch { get; set; }
    public DispatchState Outcome { get; set; } = DispatchState.Accepted;
    public async Task<DispatchAgentResult> DispatchAsync(PreparedDispatch request, string credential, CancellationToken cancellationToken)
    {
        lock (Calls) { Calls.Add(request); }
        if (OnDispatch is not null) { await OnDispatch(request); }
        return new(request.TargetAgent, request.HandoffId, Outcome,
            AcceptedBy: Outcome == DispatchState.Accepted ? "chatgpt" : null,
            ErrorCode: Outcome == DispatchState.Accepted ? null : GatewayError.UpstreamOutcomeUnknown);
    }
}

/// <summary>Conditional, thread-safe fault injection. Actual-adapter behavior is separately tested against Azurite.</summary>
internal sealed class JobTestRepository<T> : IRepository<T> where T : class
{
    private readonly object gate = new();
    private readonly Dictionary<(string, string), T> rows = [];
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public int LoseResponses { get; set; }
    public bool DropWrites { get; set; }
    public Action<T>? BeforeUpdate { get; set; }
    public Action<T>? AfterStore { get; set; }
    public Exception? ReadFailure { get; set; }
    private static T Clone(T row) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(row))!;
    private static (string, string) Key(T row) => (EntityMappingHelper.GetField<string>(row, EntityKeyKind.PartitionKey), EntityMappingHelper.GetField<string>(row, EntityKeyKind.RowKey));
    private static string? Tag(T row) => (string?)typeof(T).GetProperty("ETag")!.GetValue(row);
    private static void Tag(T row, string? value) => typeof(T).GetProperty("ETag")!.SetValue(row, value);

    public void Mutate(string partition, string row, Func<T, T> change)
    {
        lock (gate)
        {
            T value = change(Clone(rows[(partition, row)]));
            Tag(value, Guid.NewGuid().ToString("N"));
            rows[(partition, row)] = value;
        }
    }
    public Task<T?> RetrieveAsync(string partitionKey, string rowKey, IList<string>? fields = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            Reads++;
            if (ReadFailure is not null) { throw ReadFailure; }
            return Task.FromResult(rows.TryGetValue((partitionKey, rowKey), out T? row) ? Clone(row) : null);
        }
    }
    public Task<bool> ExistsAsync(string partitionKey, string rowKey, CancellationToken cancellationToken = default)
    { lock (gate) { return Task.FromResult(rows.ContainsKey((partitionKey, rowKey))); } }
    public Task<IEnumerable<T>> CreateAsync(IEnumerable<T> entities, bool replaceIfExist = false, DateTimeOffset? expiry = null, CancellationToken cancellationToken = default)
    {
        Assert.IsFalse(replaceIfExist);
        return Write(entities, true, cancellationToken);
    }
    public Task<IEnumerable<T>> UpdateAsync(IEnumerable<T> entities, bool preconditionCheck = true, bool mergeIfExists = false, CancellationToken cancellationToken = default)
    {
        Assert.IsTrue(preconditionCheck); Assert.IsFalse(mergeIfExists);
        return Write(entities, false, cancellationToken);
    }
    private Task<IEnumerable<T>> Write(IEnumerable<T> entities, bool create, CancellationToken token)
    {
        T[] values = entities.ToArray();
        foreach (T value in values)
        {
            token.ThrowIfCancellationRequested();
            if (!create && BeforeUpdate is Action<T> action) { BeforeUpdate = null; action(value); }
            lock (gate)
            {
                bool exists = rows.TryGetValue(Key(value), out T? current);
                if (create ? exists : !exists || Tag(current!) != Tag(value) || Tag(value) == "*") { throw new IOException("conditional conflict"); }
                if (DropWrites) { continue; }
                T stored = Clone(value); Tag(stored, Guid.NewGuid().ToString("N"));
                rows[Key(stored)] = stored; Writes++;
                AfterStore?.Invoke(stored);
                if (LoseResponses > 0) { LoseResponses--; throw new IOException("lost response"); }
            }
        }
        T[] response = values.Select(Clone).ToArray();
        foreach (T value in response) { Tag(value, null); }
        return Task.FromResult<IEnumerable<T>>(response);
    }
    public Task DeleteAsync(IEnumerable<T> entities, bool preconditionCheck = true, bool successIfNotExist = true, CancellationToken cancellationToken = default)
    { lock (gate) { foreach (T row in entities) { rows.Remove(Key(row)); } } return Task.CompletedTask; }
    public Task<EntityBag<T>> GetAsync(string partitionKey, int limit, string? continuationToken = null, IList<string>? fields = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            T[] found = rows.Where(p => p.Key.Item1 == partitionKey && (continuationToken is null || string.CompareOrdinal(p.Key.Item2, continuationToken) > 0))
                .OrderBy(p => p.Key.Item2, StringComparer.Ordinal).Select(p => Clone(p.Value)).Take(limit + 1).ToArray();
            T[] page = found.Take(limit).ToArray();
            return Task.FromResult(new EntityBag<T>(page, found.Length > limit ? Key(page[^1]).Item2 : null));
        }
    }
    public Task<EntityBag<T>> GetAsync(int limit, string? continuationToken = null, IList<string>? fields = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<T> GetAsync(IList<string>? fields = null, CancellationToken cancellationToken = default) => Enumerate(null, cancellationToken);
    public IAsyncEnumerable<T> GetAsync(string partitionKey, IList<string>? fields = null, CancellationToken cancellationToken = default) => Enumerate(partitionKey, cancellationToken);
    private async IAsyncEnumerable<T> Enumerate(string? partition, [EnumeratorCancellation] CancellationToken token)
    {
        await Task.CompletedTask;
        T[] all;
        lock (gate) { all = rows.Where(p => partition is null || p.Key.Item1 == partition).Select(p => Clone(p.Value)).ToArray(); }
        foreach (T row in all) { token.ThrowIfCancellationRequested(); yield return row; }
    }
}
