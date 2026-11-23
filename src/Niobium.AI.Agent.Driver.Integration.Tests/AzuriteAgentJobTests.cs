using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Flows;
using Niobium.AI.Agent.Driver.Core.Jobs;
using Niobium.AI.Agent.Driver.Server.Security;
using Niobium.AI.Agent.Driver.Server.Tools;
using Niobium.Database.StorageTable;
using System.Text.Json;

namespace Niobium.AI.Agent.Driver.Integration.Tests;

[TestClass]
public sealed class AzuriteAgentJobTests
{
    [TestMethod]
    public async Task RealAdapterEnforcesClaimsPreservesRacingResultsAndPhysicallyPurgesPayloads()
    {
        // Fixed test-only emulator. No environment input may redirect these mutations to Azure.
        TableServiceClient service = new("UseDevelopmentStorage=true");
        string table = "GatewayJob" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90)); CancellationToken token = deadline.Token;
        await service.CreateTableAsync(table, token);
        try
        {
            CloudTableRepository<AgentJob> repository = new(new LocalFactory(service), NullLogger<CloudTableRepository<AgentJob>>.Instance) { TableName = table };
            TrackedJobOptions settings = Settings(); JobCapabilities capabilities = new(Options.Create(settings));
            CallerContext owner = Caller("owner"); CallerContext writer = Caller("writer");
            DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            AgentJob intended = Intent(owner, writer, settings, now);
            AgentJobDomain Domain()
            {
                AgentJobDomain domain = new(new(() => repository), []); domain.Initialize(intended.WorkspaceShard, intended.JobId); return domain;
            }
            AgentJob created = await Domain().RegisterAsync(intended, token);
            Assert.IsNotNull(created.ETag);
            AgentJob[] same = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Domain().RegisterAsync(intended, token)));
            Assert.IsTrue(same.All(row => row.JobId == created.JobId));
            AgentJob?[] claims = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
                Domain().ClaimDispatchAsync(Guid.NewGuid().ToString("N"), now, 60, token)));
            Assert.AreEqual(1, claims.Count(row => row is not null)); AgentJob claim = claims.Single(row => row is not null)!;
            RequestFailedException stale = await Assert.ThrowsAsync<RequestFailedException>(async () =>
                _ = await repository.UpdateAsync([created], cancellationToken: token));
            Assert.AreEqual(412, stale.Status);
            SubmitJobResultRequest completion = new()
            {
                JobId = created.JobId, CompletionCapability = capabilities.Issue(created), Outcome = JobOutcome.Succeeded,
                Result = JsonSerializer.SerializeToElement(new { summary = "Local synthetic result", data = new { marker = 42 } })
            };
            // Competing callback and acceptance writes must retain both facts regardless of order.
            Task<JobResultReceipt> callback = Domain().SubmitAsync(writer, completion, capabilities, settings, now, token);
            Task<AgentJob> acceptance = Domain().RecordDispatchAsync(claim,
                new(created.TargetAgent, created.HandoffId, DispatchState.Accepted), now, token);
            await Task.WhenAll(callback, acceptance);
            AgentJob stored = (await Domain().ReadCurrentAsync(token))!;
            Assert.AreEqual(JobDispatchState.Accepted, (JobDispatchState)stored.DispatchStateCode);
            Assert.AreEqual(JobResultState.Available, (JobResultState)stored.ResultStateCode);
            Assert.IsTrue((await Domain().SubmitAsync(writer, completion, capabilities, settings, now, token)).Duplicate);
            TableClient rawTable = service.GetTableClient(table);
            TableEntity before = (await rawTable.GetEntityAsync<TableEntity>(stored.WorkspaceShard, stored.JobId, cancellationToken: token)).Value;
            Assert.IsTrue(before.ContainsKey(nameof(AgentJob.RequestJson))); Assert.IsTrue(before.ContainsKey(nameof(AgentJob.ResultJson)));
            Assert.IsFalse(before.Values.OfType<string>().Any(value => value.Contains(completion.CompletionCapability, StringComparison.Ordinal)));
            AgentJob purged = await Domain().MaintainAsync(now.AddDays(31), 30, token);
            Assert.AreEqual(JobResultState.Purged, (JobResultState)purged.ResultStateCode);
            TableEntity after = (await rawTable.GetEntityAsync<TableEntity>(stored.WorkspaceShard, stored.JobId, cancellationToken: token)).Value;
            Assert.IsFalse(after.ContainsKey(nameof(AgentJob.RequestJson)), "Replacement must remove the stored input property, not merge null into it.");
            Assert.IsFalse(after.ContainsKey(nameof(AgentJob.ResultJson)), "Replacement must remove the stored result property.");
            Assert.AreEqual(stored.ResultHash, after.GetString(nameof(AgentJob.ResultHash)));
            Assert.AreEqual(JobResultState.Purged, (JobResultState)(await Domain().RegisterAsync(intended, token)).ResultStateCode);
            Assert.IsNull(await Domain().ClaimDispatchAsync(Guid.NewGuid().ToString("N"), now.AddDays(31), 60, token));
        }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(15)); await service.DeleteTableAsync(table, cleanup.Token);
        }
    }

    [TestMethod]
    public async Task RealAdapterPagesBoundedPartitionsAndPersistsFencedMaintenanceCursors()
    {
        TableServiceClient service = new("UseDevelopmentStorage=true");
        string jobsTable = "GatewaySweepJobs" + Guid.NewGuid().ToString("N");
        string maintenanceTable = "GatewaySweep" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90)); CancellationToken token = deadline.Token;
        await service.CreateTableAsync(jobsTable, token); await service.CreateTableAsync(maintenanceTable, token);
        try
        {
            LocalFactory factory = new(service);
            CloudTableRepository<AgentJob> jobs = new(factory, NullLogger<CloudTableRepository<AgentJob>>.Instance) { TableName = jobsTable };
            CloudTableRepository<AgentMaintenance> cursors = new(factory, NullLogger<CloudTableRepository<AgentMaintenance>>.Instance) { TableName = maintenanceTable };
            AgentMaintenanceDomain Domain()
            {
                AgentMaintenanceDomain domain = new(new(() => cursors), [], new(() => jobs)); domain.Initialize("adapter-jobs", "jobs-v1"); return domain;
            }
            DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            // Synthetic IDs deliberately share shard zero to prove continuation between pages.
            for (int i = 1; i <= 3; i++)
            {
                AgentJob row = Intent(Caller("owner"), Caller("writer"), Settings(), now) with
                { JobId = "job_v1_0" + i.ToString("x").PadLeft(63, '0'), WorkspaceShard = "adapter-jobs:0" };
                _ = await jobs.CreateAsync([row], cancellationToken: token);
            }
            AgentMaintenance?[] claims = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Domain().ClaimAsync("adapter-jobs", Guid.NewGuid().ToString("N"), now, token)));
            Assert.AreEqual(1, claims.Count(row => row is not null)); AgentMaintenance claim = claims.Single(row => row is not null)!;
            EntityBag<AgentJob> page = await Domain().PageAsync(claim, token);
            Assert.AreEqual(1, page.Count); Assert.IsNotNull(page.ContinuationToken);
            AgentMaintenance checkpoint = (await Domain().CheckpointAsync(claim, page.ContinuationToken, now, false, token))!;
            Assert.IsNotNull(checkpoint.ETag); Assert.AreNotEqual(claim.ETag, checkpoint.ETag);
            EntityBag<AgentJob> next = await Domain().PageAsync(checkpoint, token);
            Assert.AreEqual(1, next.Count); Assert.AreNotEqual(page[0].JobId, next[0].JobId);
            Assert.IsNull(await Domain().CheckpointAsync(claim with { Generation = claim.Generation + 1 }, null, now, false, token));
            AgentMaintenance released = (await Domain().CheckpointAsync(checkpoint, checkpoint.Cursor, now, true, token))!;
            Assert.IsNull(released.LeaseOwner);
            Assert.AreEqual(checkpoint.Cursor, released.Cursor);
        }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(15));
            await service.DeleteTableAsync(jobsTable, cleanup.Token); await service.DeleteTableAsync(maintenanceTable, cleanup.Token);
        }
    }

    [TestMethod]
    public void ActualJobCompositionIsRegisteredButDisabledWithoutProvisionedJobTablesOrKeys()
    {
        using IServiceScope scope = IntegrationHostFixture.Host.Services.CreateScope(); IServiceProvider services = scope.ServiceProvider;
        Assert.IsInstanceOfType<CloudTableRepository<AgentJob>>(services.GetRequiredService<IRepository<AgentJob>>());
        Assert.IsInstanceOfType<CloudTableRepository<AgentMaintenance>>(services.GetRequiredService<IRepository<AgentMaintenance>>());
        services.GetRequiredService<IDomainRepository<AgentJobDomain, AgentJob>>();
        services.GetRequiredService<IDomainRepository<AgentMaintenanceDomain, AgentMaintenance>>();
        services.GetRequiredService<AgentJobFlow>(); services.GetRequiredService<AgentJobMaintenanceFlow>(); services.GetRequiredService<AgentJobTools>();
        services.GetRequiredService<IJobCapabilities>(); services.GetRequiredService<IOptions<TrackedJobOptions>>().Value.ToString();
        AgentGatewayOptions options = services.GetRequiredService<IOptions<AgentGatewayOptions>>().Value;
        Assert.IsFalse(options.EnableTrackedJobs); Assert.IsFalse(options.EnableTrackedJobAccess); Assert.IsFalse(options.EnableJobMaintenance);
    }

    private static TrackedJobOptions Settings() => new()
    {
        IdentityKey = Convert.ToBase64String(Enumerable.Repeat((byte)17, 32).ToArray()), ActiveSigningKeyVersion = "key-v1",
        SigningKeys = new() { ["key-v1"] = Convert.ToBase64String(Enumerable.Repeat((byte)34, 32).ToArray()) }
    };
    private static CallerContext Caller(string subject) => CallerContext.FromValidatedClaims("adapter-jobs", "issuer", "tenant", subject, "client", true);
    private static AgentJob Intent(CallerContext owner, CallerContext writer, TrackedJobOptions settings, DateTimeOffset now)
    {
        string id = AgentJob.Identity(owner, "research-v1", "integration-job", settings.IdentityKey);
        string json = JsonSerializer.Serialize(new StartAgentJobRequest
        {
            TargetAgent = "research-v1", HandoffId = "integration-job", Input = new() { Task = "Synthetic local work" },
            ResultSchema = JobWire.ResultSchemaId, CompletionMode = JobCompletionMode.StoreOnly
        }, GatewayJson.Options);
        return new()
        {
            WorkspaceShard = AgentJob.PartitionFor(owner.WorkspaceKey, id), JobId = id, WorkspaceKey = owner.WorkspaceKey,
            InitiatorPrincipalKey = owner.PrincipalKey, TargetAgent = "research-v1", HandoffId = "integration-job",
            RequestJson = json, RequestFingerprint = JobWire.Hash(json), ApiChannelId = "agtch_synthetic", CredentialAlias = "synthetic-token",
            ResultSchema = JobWire.ResultSchemaId, ResultWriterPrincipalKey = writer.PrincipalKey, SigningKeyVersion = "key-v1",
            CapabilityNonce = Guid.NewGuid().ToString("N"), CreatedUtc = now, ResultDeadlineUtc = now.AddDays(1), CapabilityExpiresUtc = now.AddDays(1).AddHours(1)
        };
    }
    private sealed class LocalFactory(TableServiceClient service) : IAzureTableClientFactory
    {
        public Task<TableServiceClient> CreateClientAsync(IEnumerable<DatabasePermissions> permissions, string table, string? partition = null,
            CancellationToken cancellationToken = default) => Task.FromResult(service);
    }
}
