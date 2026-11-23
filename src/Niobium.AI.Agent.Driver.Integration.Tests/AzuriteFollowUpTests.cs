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
using Niobium.Database.StorageTable;
using System.Text.Json;

namespace Niobium.AI.Agent.Driver.Integration.Tests;

[TestClass]
public sealed class AzuriteFollowUpTests
{
    [TestMethod]
    public async Task ActualAdapterPreservesAtomicOutboxAndFencesPollAndContinuationClaims()
    {
        TableServiceClient service = new("UseDevelopmentStorage=true");
        string table = "FollowUpJobs" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90)); CancellationToken token = timeout.Token;
        await service.CreateTableAsync(table, token);
        try
        {
            CloudTableRepository<AgentJob> repository = new(new LocalFactory(service), NullLogger<CloudTableRepository<AgentJob>>.Instance) { TableName = table };
            CallerContext owner = CallerContext.FromValidatedClaims("adapter-followup", "issuer", "tenant", "owner", "client", true);
            CallerContext writer = CallerContext.FromValidatedClaims("adapter-followup", "issuer", "tenant", "writer", "client-b", true);
            DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            TrackedJobOptions options = new()
            {
                IdentityKey = Convert.ToBase64String(Enumerable.Repeat((byte)17, 32).ToArray()), ActiveSigningKeyVersion = "key-v1",
                SigningKeys = new() { ["key-v1"] = Convert.ToBase64String(Enumerable.Repeat((byte)34, 32).ToArray()) }
            };
            JobCapabilities capabilities = new(Options.Create(options));
            string id = AgentJob.Identity(owner, "research-v1", "test-followup", options.IdentityKey);
            StartAgentJobRequest request = new()
            { TargetAgent = "research-v1", HandoffId = "test-followup", Input = new() { Task = "Synthetic" }, ResultSchema = JobWire.ResultSchemaId,
                CompletionMode = JobCompletionMode.NotifyParent, ContinuationRef = "parent-route-v1" };
            string json = JsonSerializer.Serialize(request, GatewayJson.Options);
            AgentJob intended = new()
            {
                Version = 2, JobId = id, WorkspaceKey = owner.WorkspaceKey, WorkspaceShard = AgentJob.PartitionFor(owner.WorkspaceKey, id),
                InitiatorPrincipalKey = owner.PrincipalKey, ResultWriterPrincipalKey = writer.PrincipalKey, TargetAgent = request.TargetAgent,
                HandoffId = request.HandoffId, RequestJson = json, RequestFingerprint = JobWire.Hash(json), ApiChannelId = "agtch_test",
                CredentialAlias = "synthetic-token", ResultSchema = JobWire.ResultSchemaId, SigningKeyVersion = "key-v1", CapabilityNonce = Guid.NewGuid().ToString("N"),
                CreatedUtc = now, ResultDeadlineUtc = now.AddDays(1), CapabilityExpiresUtc = now.AddDays(1).AddHours(1), ObserveProviderRun = true,
                CompletionModeCode = 1, ContinuationRef = "parent-route-v1", ParentTargetAgent = "parent-v1", ParentApiChannelId = "agtch_parent",
                ParentCredentialAlias = "synthetic-token", ParentConversationKey = "actual-synthetic-parent", ContinuationExpiresUtc = now.AddDays(2)
            };
            AgentJobDomain Domain()
            { AgentJobDomain domain = new(new(() => repository), []); domain.Initialize(intended.WorkspaceShard, id); return domain; }
            await Domain().RegisterAsync(intended, token);
            AgentJob dispatch = (await Domain().ClaimDispatchAsync(Guid.NewGuid().ToString("N"), now, 60, token))!;
            SubmitJobResultRequest completion = new() { JobId = id, CompletionCapability = capabilities.Issue(intended), Outcome = JobOutcome.Succeeded,
                Result = JsonSerializer.SerializeToElement(new { summary = "Synthetic result", data = new { value = 1 } }) };
            await Task.WhenAll(Domain().RecordDispatchAsync(dispatch, new("research-v1", "test-followup", DispatchState.Accepted), now, token, "apirun_test"),
                Domain().SubmitAsync(writer, completion, capabilities, options, now, token));
            AgentJob stored = (await Domain().ReadCurrentAsync(token))!;
            Assert.AreEqual("pending", stored.Status().ContinuationState); Assert.AreEqual("apirun_test", stored.ProviderRunId);
            Assert.AreEqual(JobResultState.Available, stored.Status().ResultState);
            AgentJob?[] continuations = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Domain().ClaimContinuationAsync(Guid.NewGuid().ToString("N"), now, 60, token)));
            Assert.AreEqual(1, continuations.Count(c => c is not null)); AgentJob notification = continuations.Single(c => c is not null)!;
            AgentJob?[] polls = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Domain().ClaimObservationAsync(Guid.NewGuid().ToString("N"), now, token)));
            Assert.AreEqual(1, polls.Count(c => c is not null)); AgentJob poll = polls.Single(c => c is not null)!;
            await Task.WhenAll(Domain().RecordContinuationAsync(notification, new("parent-v1", "event", DispatchState.Accepted), now, token),
                Domain().RecordObservationAsync(poll, new(JobProviderState.Completed), now, token));
            AgentJob done = (await Domain().ReadCurrentAsync(token))!;
            Assert.AreEqual("completed", done.Status().ProviderState); Assert.AreEqual("accepted", done.Status().ContinuationState);
            Assert.AreEqual(stored.ResultHash, done.ResultHash);
            AgentJob purged = await Domain().MaintainAsync(now.AddDays(31), 30, token);
            Assert.AreEqual(JobResultState.Purged, purged.Status().ResultState);
            TableEntity raw = (await service.GetTableClient(table).GetEntityAsync<TableEntity>(intended.WorkspaceShard, id, cancellationToken: token)).Value;
            Assert.IsFalse(raw.ContainsKey(nameof(AgentJob.ParentConversationKey))); Assert.IsFalse(raw.ContainsKey(nameof(AgentJob.ResultJson)));
            Assert.AreEqual("accepted", purged.Status().ContinuationState); Assert.AreEqual("completed", purged.Status().ProviderState);
        }
        finally { using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(15)); await service.DeleteTableAsync(table, cleanup.Token); }
    }

    [TestMethod]
    public async Task ActualRouteAdapterRejectsRebindingAndCompositionKeepsFollowUpsDisabled()
    {
        TableServiceClient service = new("UseDevelopmentStorage=true"); string table = "FollowUpRoutes" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60)); CancellationToken token = timeout.Token;
        await service.CreateTableAsync(table, token);
        try
        {
            CloudTableRepository<AgentContinuationRoute> repository = new(new LocalFactory(service), NullLogger<CloudTableRepository<AgentContinuationRoute>>.Instance) { TableName = table };
            CallerContext owner = CallerContext.FromValidatedClaims("adapter-route", "issuer", "tenant", "owner", "client", true);
            AgentGatewayOptions options = new() { InternalWorkspaceKey = owner.WorkspaceKey, CredentialAliases = new() { ["synthetic"] = "test-only" } };
            AgentContinuationRoute route = new() { WorkspaceKey = owner.WorkspaceKey, ContinuationRef = "parent-v1", Enabled = true,
                InitiatorPrincipalKey = owner.PrincipalKey, ParentTargetAgent = "parent-v1", ApiChannelId = "agtch_parent", CredentialAlias = "synthetic",
                ProviderConversationKey = "synthetic-conversation", ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1) };
            AgentContinuationRouteDomain domain = new(new(() => repository), []); domain.Initialize(route.WorkspaceKey, route.ContinuationRef);
            AgentContinuationRoute saved = await domain.ConfigureAsync(route, null, options, token);
            Assert.AreEqual(route.ProviderConversationKey, (await domain.GetAuthorizedAsync(owner, options, DateTimeOffset.UtcNow, token)).ProviderConversationKey);
            await Assert.ThrowsExactlyAsync<GatewayException>(() => domain.ConfigureAsync(route with { ApiChannelId = "agtch_other" }, saved.ETag, options, token));
            await domain.ConfigureAsync(route with { Enabled = false }, saved.ETag, options, token);
            await Assert.ThrowsExactlyAsync<JobException>(() => domain.GetAuthorizedAsync(owner, options, DateTimeOffset.UtcNow, token));
        }
        finally { using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(15)); await service.DeleteTableAsync(table, cleanup.Token); }
        using IServiceScope scope = IntegrationHostFixture.Host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentJobFollowUpFlow>();
        scope.ServiceProvider.GetRequiredService<IDomainRepository<AgentContinuationRouteDomain, AgentContinuationRoute>>();
        AgentGatewayOptions configured = scope.ServiceProvider.GetRequiredService<IOptions<AgentGatewayOptions>>().Value;
        Assert.IsFalse(configured.EnableParentContinuation); Assert.IsFalse(configured.EnableProviderRunPolling);
    }

    private sealed class LocalFactory(TableServiceClient service) : IAzureTableClientFactory
    {
        public Task<TableServiceClient> CreateClientAsync(IEnumerable<DatabasePermissions> permissions, string table, string? partition = null,
            CancellationToken cancellationToken = default) => Task.FromResult(service);
    }
}
