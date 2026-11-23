using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;

namespace Niobium.AI.Agent.Driver.Server.Tests;

[TestClass]
public sealed class DomainAndFlowTests
{
    [TestMethod]
    public async Task DispatchAndListUseCurrentGrantsAndNeverPersistHandoffs()
    {
        GatewayFixture fixture = new();
        await fixture.SeedAsync();
        ListAgentsResult list = await fixture.ListFlow().ExecuteAsync(fixture.Caller, default);
        Assert.AreEqual(1, list.Agents.Count);
        Assert.AreEqual("Research", list.Agents[0].DisplayName);
        Assert.AreEqual("Synthetic test target", list.Agents[0].Description);
        var flow = fixture.DispatchFlow();
        Assert.AreEqual(DispatchState.Accepted, (await flow.ExecuteAsync(fixture.Request, fixture.Caller, default)).DispatchState);
        AgentGrant grant = (await fixture.Grants.RetrieveAsync(fixture.Grant.CallerPartition, "research-v1"))!;
        await fixture.Grants.UpdateAsync([grant with { Enabled = false }]);
        GatewayException denied = await Assert.ThrowsExactlyAsync<GatewayException>(() => flow.ExecuteAsync(fixture.Request, fixture.Caller, default));
        Assert.AreEqual(GatewayError.Forbidden, denied.Code);
        Assert.AreEqual(1, fixture.Provider.Calls.Count);
        Assert.AreEqual(1, fixture.Registrations.Writes);
        Assert.AreEqual(2, fixture.Grants.Writes);
        Assert.AreEqual(0, (await fixture.ListFlow().ExecuteAsync(fixture.Caller, default)).Agents.Count);
    }

    [TestMethod]
    public async Task MissingGrantAndCrossWorkspaceNeverReachProvider()
    {
        GatewayFixture fixture = new();
        Assert.AreEqual(0, (await fixture.ListFlow().ExecuteAsync(fixture.Caller, default)).Agents.Count);
        await Assert.ThrowsExactlyAsync<GatewayException>(() => fixture.DispatchFlow().ExecuteAsync(fixture.Request, fixture.Caller, default));
        await fixture.SeedAsync();
        await Assert.ThrowsExactlyAsync<GatewayException>(() => fixture.DispatchFlow().ExecuteAsync(
            fixture.Request, fixture.Caller with { WorkspaceKey = "different-workspace" }, default));
        Assert.AreEqual(0, fixture.Provider.Calls.Count);
    }

    [TestMethod]
    public async Task LookupFailureAndCancellationAreNotDispatch()
    {
        GatewayFixture fixture = new();
        fixture.Grants.ReadFailure = new IOException("private storage text that must not escape");
        DispatchAgentResult result = await fixture.DispatchFlow().ExecuteAsync(fixture.Request, fixture.Caller, default);
        Assert.AreEqual(DispatchState.NotDispatched, result.DispatchState);
        Assert.AreEqual(GatewayError.ConfigurationUnavailable, result.ErrorCode);
        fixture.Grants.ReadFailure = new OperationCanceledException();
        result = await fixture.DispatchFlow().ExecuteAsync(fixture.Request, fixture.Caller, default);
        Assert.AreEqual(GatewayError.DeadlineExceeded, result.ErrorCode);
        Assert.AreEqual(0, fixture.Provider.Calls.Count);
    }

    [TestMethod]
    public async Task MissingDisabledUnknownVersionAndUnallowlistedTargetsAreNotListed()
    {
        foreach (AgentRegistration? registration in new AgentRegistration?[]
        {
            null,
            new GatewayFixture().Registration with { Enabled = false },
            new GatewayFixture().Registration with { Version = 99 },
            new GatewayFixture().Registration with { CredentialAlias = "unknown-token" }
        })
        {
            GatewayFixture fixture = new();
            await fixture.Grants.CreateAsync([fixture.Grant]);
            if (registration is not null) { await fixture.Registrations.CreateAsync([registration]); }
            Assert.AreEqual(0, (await fixture.ListFlow().ExecuteAsync(fixture.Caller, default)).Agents.Count);
            GatewayException error = await Assert.ThrowsExactlyAsync<GatewayException>(() => fixture.DispatchFlow().ExecuteAsync(fixture.Request, fixture.Caller, default));
            Assert.AreEqual(GatewayError.TargetUnavailable, error.Code);
        }
    }

    [TestMethod]
    public async Task GrantExpiryVersionAndPermissionFailClosed()
    {
        GatewayFixture fixture = new();
        foreach (AgentGrant value in new[]
        {
            fixture.Grant with { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
            fixture.Grant with { Version = 99 }, fixture.Grant with { CanDispatch = false },
            fixture.Grant with { CallerPartition = "wrong" }, fixture.Grant with { TargetAgent = "/bad" }
        })
        {
            AgentGrantDomain domain = fixture.GrantDomain();
            domain.Initialize(value);
            Assert.IsFalse(await domain.PermitsDispatchAsync(fixture.Caller, DateTimeOffset.UtcNow, default));
        }
        AgentGrantDomain valid = fixture.GrantDomain();
        valid.Initialize(fixture.Grant with { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) });
        Assert.IsTrue(await valid.PermitsDispatchAsync(fixture.Caller, DateTimeOffset.UtcNow, default));
        AgentGrantDomain missing = fixture.GrantDomain();
        missing.Initialize("missing", "missing");
        Assert.IsFalse(await missing.PermitsDispatchAsync(fixture.Caller, DateTimeOffset.UtcNow, default));
    }

    [TestMethod]
    public async Task ListingHasBoundedWork()
    {
        GatewayFixture fixture = new();
        fixture.Options.MaxRegistryEntries = 1;
        await fixture.Grants.CreateAsync([fixture.Grant, fixture.Grant with { TargetAgent = "other-v1" }]);
        GatewayException error = await Assert.ThrowsExactlyAsync<GatewayException>(() => fixture.ListFlow().ExecuteAsync(fixture.Caller, default));
        Assert.AreEqual(GatewayError.RegistryLimitExceeded, error.Code);
    }

    [TestMethod]
    public async Task OperatorWritesRequireExactEtagsAndReadBackTheCompleteSnapshot()
    {
        GatewayFixture fixture = new();
        AgentRegistrationDomain registrationDomain = fixture.RegistrationDomain();
        AgentRegistration created = await registrationDomain.ConfigureAsync(fixture.Registration, null, fixture.Options);
        Assert.IsFalse(string.IsNullOrEmpty(created.ETag));
        Assert.IsFalse(string.IsNullOrEmpty(created.WriteId));
        AgentRegistration updated = await registrationDomain.ConfigureAsync(created with { Enabled = false }, created.ETag, fixture.Options);
        Assert.AreNotEqual(created.ETag, updated.ETag);
        Assert.AreNotEqual(created.WriteId, updated.WriteId);
        await Assert.ThrowsExactlyAsync<GatewayException>(() => registrationDomain.ConfigureAsync(created, created.ETag, fixture.Options));
        await Assert.ThrowsExactlyAsync<GatewayException>(() => registrationDomain.ConfigureAsync(created, null, fixture.Options));
        await Assert.ThrowsExactlyAsync<GatewayException>(() => registrationDomain.ConfigureAsync(updated with { ApiChannelId = "agtch_newchannel" }, updated.ETag, fixture.Options));
        await Assert.ThrowsExactlyAsync<GatewayException>(() => registrationDomain.ConfigureAsync(updated, "*", fixture.Options));
        await Assert.ThrowsExactlyAsync<GatewayException>(() => registrationDomain.ConfigureAsync(updated with { Version = 2 }, updated.ETag, fixture.Options));
        fixture.Registrations.AfterWrite = row => row.Description = "concurrent mutation";
        await Assert.ThrowsExactlyAsync<GatewayException>(() => registrationDomain.ConfigureAsync(updated with { Enabled = true }, updated.ETag, fixture.Options));

        AgentGrantDomain grantDomain = fixture.GrantDomain();
        AgentGrant grant = await grantDomain.ConfigureAsync(fixture.Grant, null, fixture.Options);
        AgentGrant revoked = await grantDomain.ConfigureAsync(grant with { CanDispatch = false }, grant.ETag, fixture.Options);
        Assert.IsFalse(revoked.CanDispatch);
        Assert.AreNotEqual(grant.ETag, revoked.ETag);
        await Assert.ThrowsExactlyAsync<GatewayException>(() => grantDomain.ConfigureAsync(grant, grant.ETag, fixture.Options));
        await Assert.ThrowsExactlyAsync<GatewayException>(() => grantDomain.ConfigureAsync(grant, null, fixture.Options));
        await Assert.ThrowsExactlyAsync<GatewayException>(() => grantDomain.ConfigureAsync(revoked, "*", fixture.Options));
        await Assert.ThrowsExactlyAsync<GatewayException>(() => grantDomain.ConfigureAsync(revoked with { CallerPartition = "untrusted" }, null, fixture.Options));
        fixture.Grants.AfterWrite = row => row.CanDispatch = true;
        await Assert.ThrowsExactlyAsync<GatewayException>(() => grantDomain.ConfigureAsync(revoked, revoked.ETag, fixture.Options));
    }

    [TestMethod]
    public void RegistrationValidationBoundsEveryConfigurationField()
    {
        GatewayFixture fixture = new();
        foreach (AgentRegistration registration in new[]
        {
            fixture.Registration with { WorkspaceKey = "foreign" }, fixture.Registration with { TargetAgent = "/bad" },
            fixture.Registration with { ApiChannelId = "https://untrusted.invalid" }, fixture.Registration with { CredentialAlias = "wrong" },
            fixture.Registration with { DisplayName = new string('x', 161) }, fixture.Registration with { Description = new string('x', 513) },
            fixture.Registration with { MaxInputBytes = 0 }, fixture.Registration with { MaxInputBytes = 16385 }
        }) { Assert.IsFalse(AgentRegistrationDomain.IsValid(registration, fixture.Options)); }
    }
}
