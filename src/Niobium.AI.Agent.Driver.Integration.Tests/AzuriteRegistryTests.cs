using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.Database.StorageTable;

namespace Niobium.AI.Agent.Driver.Integration.Tests;

[TestClass]
public sealed class AzuriteRegistryTests
{
    [TestMethod]
    public async Task PinnedAdapterRoundTripsKeysEtagsConditionalUpdatesAndGrantExpiry()
    {
        // A fixed emulator connection is intentionally confined to this test assembly.
        // No environment variable can redirect these mutation tests to an Azure account.
        TableServiceClient service = new("UseDevelopmentStorage=true");
        string registrationTable = "GatewayReg" + Guid.NewGuid().ToString("N");
        string grantTable = "GatewayGrant" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        await service.CreateTableAsync(registrationTable, token);
        try
        {
            await service.CreateTableAsync(grantTable, token);
            LocalFactory factory = new(service);
            CloudTableRepository<AgentRegistration> registrations = new(factory,
                NullLogger<CloudTableRepository<AgentRegistration>>.Instance) { TableName = registrationTable };
            CloudTableRepository<AgentGrant> grants = new(factory,
                NullLogger<CloudTableRepository<AgentGrant>>.Instance) { TableName = grantTable };
            AgentGatewayOptions options = new()
            {
                Enabled = true, InternalWorkspaceKey = "adapter-test",
                CredentialAliases = new() { ["synthetic-token"] = "synthetic-only" }
            };
            AgentRegistrationDomain domain = new(new(() => registrations), []);
            AgentRegistration created = await domain.ConfigureAsync(new()
            {
                WorkspaceKey = "adapter-test", TargetAgent = "research-v1", Enabled = true,
                ApiChannelId = "agtch_emulatortest", CredentialAlias = "synthetic-token", DisplayName = "Test", Description = "Local only"
            }, null, options, token);
            Assert.IsFalse(string.IsNullOrWhiteSpace(created.ETag));
            Assert.AreEqual("adapter-test", created.WorkspaceKey);
            Assert.AreEqual("research-v1", created.TargetAgent);
            AgentRegistration updated = await domain.ConfigureAsync(created with { Enabled = false }, created.ETag, options, token);
            Assert.AreNotEqual(created.ETag, updated.ETag);
            Assert.AreNotEqual(created.WriteId, updated.WriteId);
            await Assert.ThrowsExactlyAsync<GatewayException>(() => domain.ConfigureAsync(created, created.ETag, options, token));
            // Verify the actual pinned adapter's service-side 412, not only our pre-read guard.
            RequestFailedException stale = await Assert.ThrowsAsync<RequestFailedException>(async () =>
                _ = await registrations.UpdateAsync([created], cancellationToken: token));
            Assert.AreEqual(412, stale.Status);
            Assert.IsNull(await registrations.RetrieveAsync("other-workspace", "research-v1", cancellationToken: token));
            List<AgentRegistration> rows = [];
            await foreach (AgentRegistration row in registrations.GetAsync("adapter-test", cancellationToken: token)) { rows.Add(row); }
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(updated.ETag, rows[0].ETag);

            CallerContext caller = CallerContext.FromValidatedClaims("adapter-test", "issuer", "tenant", "subject", "client", true);
            AgentGrantDomain grantDomain = new(new(() => grants), []);
            AgentGrant grant = await grantDomain.ConfigureAsync(new()
            {
                CallerPartition = AgentGrant.PartitionFor(caller), TargetAgent = "research-v1", Enabled = true,
                CanDispatch = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
            }, null, options, token);
            Assert.IsFalse(string.IsNullOrWhiteSpace(grant.ETag));
            grantDomain.Initialize(grant.CallerPartition, grant.TargetAgent);
            Assert.IsTrue(await grantDomain.PermitsDispatchAsync(caller, DateTimeOffset.UtcNow, token));
            await grantDomain.ConfigureAsync(grant with { CanDispatch = false }, grant.ETag, options, token);
            await grantDomain.ReloadAsync(token);
            Assert.IsFalse(await grantDomain.PermitsDispatchAsync(caller, DateTimeOffset.UtcNow, token));
        }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(15));
            await service.DeleteTableAsync(registrationTable, cleanup.Token);
            await service.DeleteTableAsync(grantTable, cleanup.Token);
        }
    }

    private sealed class LocalFactory(TableServiceClient service) : IAzureTableClientFactory
    {
        public Task<TableServiceClient> CreateClientAsync(IEnumerable<DatabasePermissions> permissions,
            string table, string? partition = null, CancellationToken cancellationToken = default) => Task.FromResult(service);
    }
}
