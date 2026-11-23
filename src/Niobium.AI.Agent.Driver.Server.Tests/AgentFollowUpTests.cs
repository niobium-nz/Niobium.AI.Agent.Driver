using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Niobium.AI.Agent.Driver.Contracts;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Domains;
using Niobium.AI.Agent.Driver.Core.Entities;
using Niobium.AI.Agent.Driver.Core.Jobs;
using Niobium.AI.Agent.Driver.Server.Operations;

namespace Niobium.AI.Agent.Driver.Server.Tests;

[TestClass]
public sealed class AgentFollowUpTests
{
    [TestMethod]
    public async Task ResultAndNotificationAreAtomicAndDuplicateCallbacksNeverRequeue()
    {
        AgentFollowUpFixture f = new(); await f.SeedAsync();
        AgentJob row = await f.StartAsync();
        Assert.AreEqual("unobserved", row.Status().ProviderState);
        Assert.AreEqual("not_requested", row.Status().ContinuationState);
        f.Inner.Jobs.LoseResponses = 1;
        JobResultReceipt first = await f.Flow.SubmitAsync(f.Inner.Completion(row), f.Inner.Writer, default);
        AgentJob pending = await f.Inner.ReadAsync(row.JobId);
        Assert.AreEqual("pending", pending.Status().ContinuationState);
        Assert.AreEqual(JobResultState.Available, pending.Status().ResultState);
        byte[] original = pending.PrepareContinuation(65536).Body.ToArray();
        AgentJob sent = await f.FollowUps.ExecuteAsync(pending, default);
        Assert.AreEqual("accepted", sent.Status().ContinuationState);
        Assert.AreEqual("queued", sent.Status().ProviderState);
        Assert.AreEqual(2, f.Inner.Provider.Calls.Count);
        string outer = Encoding.UTF8.GetString(f.Inner.Provider.Calls[1].Body.Span);
        using JsonDocument body = JsonDocument.Parse(outer);
        Assert.AreEqual(f.Route.ProviderConversationKey, body.RootElement.GetProperty("conversation_key").GetString());
        using JsonDocument input = JsonDocument.Parse(body.RootElement.GetProperty("input").GetString()!);
        Assert.AreEqual("niobium.agent-result-ready.v1", input.RootElement.GetProperty("schema").GetString());
        Assert.AreEqual(first.ResultHash, input.RootElement.GetProperty("result_hash").GetString());
        Assert.IsFalse(outer.Contains("completion_capability", StringComparison.Ordinal));
        CollectionAssert.AreEqual(original, sent.PrepareContinuation(65536).Body.ToArray());
        Assert.AreNotEqual(f.Inner.Provider.Calls[0].IdempotencyKey, f.Inner.Provider.Calls[1].IdempotencyKey);
        JobResultReceipt duplicate = await f.Flow.SubmitAsync(f.Inner.Completion(row), f.Inner.Writer, default);
        Assert.IsTrue(duplicate.Duplicate);
        await f.FollowUps.ExecuteAsync(await f.Inner.ReadAsync(row.JobId), default);
        Assert.AreEqual(2, f.Inner.Provider.Calls.Count); Assert.AreEqual(1, f.Provider.Observations);
    }

    [TestMethod]
    public async Task CallbackBeforeDispatchAcknowledgmentPreservesPendingOutbox()
    {
        AgentFollowUpFixture f = new(); await f.SeedAsync();
        f.Inner.Provider.OnDispatch = async request =>
        {
            if (request.TargetAgent != f.Inner.Request.TargetAgent) { return; }
            string id = AgentJob.Identity(f.Inner.Owner, request.TargetAgent, request.HandoffId, f.Inner.Settings.IdentityKey);
            AgentJob current = await f.Inner.ReadAsync(id);
            await f.Flow.SubmitAsync(f.Inner.Completion(current), f.Inner.Writer, default);
        };
        AgentJob row = await f.StartAsync();
        Assert.AreEqual("pending", row.Status().ContinuationState);
        Assert.AreEqual(JobDispatchState.Accepted, row.Status().DispatchState);
        Assert.AreEqual(JobResultState.Available, row.Status().ResultState);
        AgentJob sent = await f.FollowUps.ExecuteAsync(row, default);
        Assert.AreEqual("accepted", sent.Status().ContinuationState);
    }

    [TestMethod]
    public async Task ConcurrentContinuationClaimsHaveOneWinnerAndFencedWritersCannotOverwrite()
    {
        AgentFollowUpFixture f = new(); await f.SeedAsync(); AgentJob row = await f.StartAsync(true);
        AgentJob?[] claims = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => f.Inner.Domain(row.JobId)
            .ClaimContinuationAsync(Guid.NewGuid().ToString("N"), f.Inner.Clock.Now, 60, default))));
        Assert.AreEqual(1, claims.Count(c => c is not null));
        AgentJob claim = claims.Single(c => c is not null)!;
        DispatchAgentResult accepted = new("parent-v1", "event", DispatchState.Accepted);
        foreach (AgentJob stale in new[] { claim with { ContinuationLeaseOwner = "other" }, claim with { ContinuationGeneration = 99 }, claim with { JobId = "other" } })
        { await f.Inner.Domain(row.JobId).RecordContinuationAsync(stale, accepted, f.Inner.Clock.Now, default); }
        Assert.AreEqual("sending", (await f.Inner.ReadAsync(row.JobId)).Status().ContinuationState);
        f.Inner.Clock.Now = f.Inner.Clock.Now.AddSeconds(61);
        await f.Inner.Domain(row.JobId).RecordContinuationAsync(claim, accepted, f.Inner.Clock.Now, default);
        AgentJob held = await f.Inner.Domain(row.JobId).MaintainAsync(f.Inner.Clock.Now, 30, default);
        Assert.AreEqual("held", held.Status().ContinuationState);
        await f.FollowUps.ExecuteAsync(held, default);
        Assert.AreEqual(1, f.Inner.Provider.Calls.Count);
    }

    [TestMethod]
    public async Task UnknownParentOutcomeIsNotResentAndPayloadIsNotPrematurelyPurged()
    {
        AgentFollowUpFixture f = new(); await f.SeedAsync(); AgentJob row = await f.StartAsync(true);
        f.Inner.Provider.Outcome = DispatchState.OutcomeUnknown;
        AgentJob unknown = await f.FollowUps.ExecuteAsync(row, default);
        Assert.AreEqual("unknown", unknown.Status().ContinuationState);
        await f.Flow.SubmitAsync(f.Inner.Completion(row), f.Inner.Writer, default);
        f.Inner.Clock.Now = f.Inner.Clock.Now.AddDays(40);
        AgentJob preserved = await f.Inner.Domain(row.JobId).MaintainAsync(f.Inner.Clock.Now, 30, default);
        Assert.IsNotNull(preserved.RequestJson); Assert.IsNotNull(preserved.ResultJson);
        await f.FollowUps.ExecuteAsync(preserved, default);
        Assert.AreEqual(2, f.Inner.Provider.Calls.Count);
    }

    [TestMethod]
    public async Task CurrentParentGrantAndRouteAreRequiredAndCannotBeRebound()
    {
        foreach (int change in Enumerable.Range(0, 3))
        {
            AgentFollowUpFixture f = new(); await f.SeedAsync(); AgentJob row = await f.StartAsync(true);
            if (change == 0) { f.Routes.Mutate(f.Route.WorkspaceKey, f.Route.ContinuationRef, route => route with { Enabled = false }); }
            if (change == 1) { f.Routes.Mutate(f.Route.WorkspaceKey, f.Route.ContinuationRef, route => route with { ProviderConversationKey = "different-parent" }); }
            if (change == 2) { await f.Inner.Legacy.Grants.DeleteAsync([f.Inner.Legacy.Grant with { TargetAgent = f.Route.ParentTargetAgent }]); }
            AgentJob held = await f.FollowUps.ExecuteAsync(row, default);
            Assert.AreEqual("held", held.Status().ContinuationState); Assert.AreEqual(1, f.Inner.Provider.Calls.Count);
        }
    }

    [TestMethod]
    public async Task ExpiredPendingRouteIsHeldWithoutExternalAction()
    {
        AgentFollowUpFixture f = new(); await f.SeedAsync(); AgentJob row = await f.StartAsync(true);
        f.Inner.Clock.Now = row.ContinuationExpiresUtc!.Value;
        AgentJob held = await f.Inner.Domain(row.JobId).MaintainAsync(f.Inner.Clock.Now, 30, default);
        Assert.AreEqual("held", held.Status().ContinuationState);
        await f.FollowUps.ExecuteAsync(held, default); Assert.AreEqual(1, f.Inner.Provider.Calls.Count);
    }

    [TestMethod]
    public async Task DisablingNewStartsAndFollowUpsPreservesCallbackAndRetrieval()
    {
        AgentFollowUpFixture f = new(); await f.SeedAsync(); AgentJob row = await f.StartAsync();
        f.Inner.Gateway.EnableTrackedJobs = false; f.Inner.Gateway.EnableProviderRunPolling = false; f.Inner.Gateway.EnableParentContinuation = false;
        await f.Flow.SubmitAsync(f.Inner.Completion(row), f.Inner.Writer, default);
        AgentJob paused = await f.FollowUps.ExecuteAsync(await f.Inner.ReadAsync(row.JobId), default);
        Assert.AreEqual("pending", paused.Status().ContinuationState);
        Assert.AreEqual(JobResultState.Available, (await f.Flow.ResultAsync(new() { JobId = row.JobId }, f.Inner.Owner, default)).ResultState);
        Assert.AreEqual(1, f.Inner.Provider.Calls.Count); Assert.AreEqual(0, f.Provider.Observations);
        f.Inner.Gateway.EnableParentContinuation = true;
        AgentJob resumed = await f.FollowUps.ExecuteAsync(paused, default);
        Assert.AreEqual("accepted", resumed.Status().ContinuationState);
    }

    [TestMethod]
    public async Task ProviderCompletionDoesNotCreateResultsAndFailureDoesNotEraseCallbacks()
    {
        AgentFollowUpFixture f = new(); await f.SeedAsync(); AgentJob row = await f.StartAsync();
        f.Provider.Observation = new(JobProviderState.Completed);
        AgentJob completed = await f.FollowUps.ExecuteAsync(row, default);
        Assert.AreEqual("completed", completed.Status().ProviderState);
        Assert.AreEqual(JobResultState.Pending, completed.Status().ResultState);
        await f.FollowUps.ExecuteAsync(completed, default); Assert.AreEqual(1, f.Provider.Observations);
        AgentFollowUpFixture g = new(); await g.SeedAsync(); AgentJob other = await g.StartAsync();
        g.Provider.OnObserve = async () => { await g.Flow.SubmitAsync(g.Inner.Completion(other), g.Inner.Writer, default); };
        g.Provider.Observation = new(JobProviderState.Failed, ProviderObservationError.RunFailed);
        AgentJob failed = await g.FollowUps.ExecuteAsync(other, default);
        Assert.AreEqual(JobResultState.Available, failed.Status().ResultState);
        Assert.AreEqual("pending", failed.Status().ContinuationState);
        Assert.AreEqual("failed", failed.Status().ProviderState);
    }

    [TestMethod]
    public async Task MissingRunMetadataNeverCausesRedispatchOrPolling()
    {
        AgentFollowUpFixture f = new(); await f.SeedAsync(); f.Provider.RunId = null;
        AgentJob row = await f.StartAsync();
        Assert.AreEqual(JobDispatchState.Accepted, row.Status().DispatchState);
        await f.FollowUps.ExecuteAsync(row, default); await f.Flow.StartAsync(f.Request, f.Inner.Owner, default);
        Assert.AreEqual(0, f.Provider.Observations); Assert.AreEqual(1, f.Inner.Provider.Calls.Count);
    }

    [TestMethod]
    public async Task PollIntervalLeaseAndGenerationPreventStaleOrRepeatedObservation()
    {
        AgentFollowUpFixture f = new(); await f.SeedAsync(); AgentJob row = await f.StartAsync();
        AgentJobDomain domain = f.Inner.Domain(row.JobId);
        AgentJob first = (await domain.ClaimObservationAsync(Guid.NewGuid().ToString("N"), f.Inner.Clock.Now, default))!;
        Assert.IsNull(await domain.ClaimObservationAsync(Guid.NewGuid().ToString("N"), f.Inner.Clock.Now, default));
        f.Inner.Clock.Now = f.Inner.Clock.Now.AddSeconds(61);
        AgentJob second = (await domain.ClaimObservationAsync(Guid.NewGuid().ToString("N"), f.Inner.Clock.Now, default))!;
        await domain.RecordObservationAsync(first, new(JobProviderState.Completed), f.Inner.Clock.Now, default);
        Assert.AreEqual("unobserved", (await f.Inner.ReadAsync(row.JobId)).Status().ProviderState);
        await domain.RecordObservationAsync(second, new(JobProviderState.Suspended), f.Inner.Clock.Now, default);
        Assert.AreEqual("suspended", (await f.Inner.ReadAsync(row.JobId)).Status().ProviderState);
        Assert.IsNull(await domain.ClaimObservationAsync(Guid.NewGuid().ToString("N"), f.Inner.Clock.Now, default));
        f.Inner.Clock.Now = f.Inner.Clock.Now.AddMinutes(1);
        AgentJob third = (await domain.ClaimObservationAsync(Guid.NewGuid().ToString("N"), f.Inner.Clock.Now, default))!;
        await domain.RecordObservationAsync(third, new(null, ProviderObservationError.RateLimited, 600), f.Inner.Clock.Now, default);
        AgentJob throttled = await f.Inner.ReadAsync(row.JobId);
        Assert.AreEqual("suspended", throttled.Status().ProviderState); Assert.AreEqual(f.Inner.Clock.Now.AddSeconds(600), throttled.NextPollUtc);
        f.Inner.Clock.Now = row.CapabilityExpiresUtc;
        Assert.IsNull(await domain.ClaimObservationAsync(Guid.NewGuid().ToString("N"), f.Inner.Clock.Now, default));
    }

    [TestMethod]
    public async Task MaintenanceResumesOnlyPendingNotificationAndUsesTheExistingBoundedCursor()
    {
        AgentFollowUpFixture f = new(); await f.SeedAsync(); AgentJob row = await f.StartAsync(true);
        f.Inner.Gateway.EnableTrackedJobs = false;
        await f.Maintenance.ExecuteAsync(default);
        AgentJob completed = await f.Inner.ReadAsync(row.JobId);
        Assert.AreEqual("accepted", completed.Status().ContinuationState);
        Assert.AreEqual(2, f.Inner.Provider.Calls.Count);
        await f.Maintenance.ExecuteAsync(default); Assert.AreEqual(2, f.Inner.Provider.Calls.Count);
    }

    [TestMethod]
    public async Task ShortRetentionDoesNotBreakAnUnexpiredDuplicateCapability()
    {
        AgentJobFixture f = new(); await f.SeedAsync(); f.Settings.ResultDeadlineMinutes = 10080; f.Settings.PayloadRetentionDays = 1;
        AgentJobStatus status = await f.Flow.StartAsync(f.Request, f.Owner, default); AgentJob row = await f.ReadAsync(status.JobId);
        JobResultReceipt first = await f.Flow.SubmitAsync(f.Completion(row), f.Writer, default);
        f.Clock.Now = f.Clock.Now.AddDays(2);
        AgentJob retained = await f.Domain(row.JobId).MaintainAsync(f.Clock.Now, 1, default);
        Assert.AreEqual(JobResultState.Available, retained.Status().ResultState);
        JobResultReceipt duplicate = await f.Flow.SubmitAsync(f.Completion(row), f.Writer, default);
        Assert.IsTrue(duplicate.Duplicate); Assert.AreEqual(first.ResultHash, duplicate.ResultHash);
        f.Clock.Now = row.CapabilityExpiresUtc;
        Assert.AreEqual(JobResultState.Purged, (await f.Domain(row.JobId).MaintainAsync(f.Clock.Now, 1, default)).Status().ResultState);
    }

    [TestMethod]
    public async Task OperatorRouteWritesAreConditionalAndImmutableAndReceiptsAreSanitized()
    {
        AgentFollowUpFixture f = new();
        AgentContinuationRouteDomain domain = new(new(() => f.Routes), []);
        f.Routes.LoseResponses = 1;
        AgentContinuationRoute created = await domain.ConfigureAsync(f.Route, null, f.Inner.Gateway, default);
        Assert.IsNotNull(created.ETag);
        await Assert.ThrowsExactlyAsync<GatewayException>(() => domain.ConfigureAsync(f.Route, null, f.Inner.Gateway, default));
        await Assert.ThrowsExactlyAsync<GatewayException>(() => domain.ConfigureAsync(f.Route, "*", f.Inner.Gateway, default));
        await Assert.ThrowsExactlyAsync<GatewayException>(() => domain.ConfigureAsync(f.Route with { ProviderConversationKey = "rebound" }, created.ETag, f.Inner.Gateway, default));
        RegistryConfigurationCommand command = new(f.Inner.Legacy.RegistrationDomains(), f.Inner.Legacy.GrantDomains(), Options.Create(f.Inner.Gateway), f.RouteDomains);
        RegistryWriteReceipt receipt = await command.ExecuteAsync(new(created.ETag, null, null, f.Route with { Enabled = false }), default);
        Assert.AreEqual("continuation", receipt.Kind);
        Assert.IsFalse(JsonSerializer.Serialize(receipt).Contains(f.Route.ProviderConversationKey, StringComparison.Ordinal));
        domain.Initialize(created.WorkspaceKey, created.ContinuationRef);
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.GetAuthorizedAsync(f.Inner.Owner, f.Inner.Gateway, f.Inner.Clock.Now, default));
        AgentContinuationRoute observed = (await f.Routes.RetrieveAsync(created.WorkspaceKey, created.ContinuationRef))!;
        _ = await domain.ConfigureAsync(observed with { Enabled = true }, observed.ETag, f.Inner.Gateway, default);
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.GetAuthorizedAsync(f.Inner.Writer, f.Inner.Gateway, f.Inner.Clock.Now, default));
        RegistryConfigurationCommand missing = new(f.Inner.Legacy.RegistrationDomains(), f.Inner.Legacy.GrantDomains(), Options.Create(f.Inner.Gateway));
        await Assert.ThrowsExactlyAsync<GatewayException>(() => missing.ExecuteAsync(new(null, null, null, f.Route), default));
        await Assert.ThrowsExactlyAsync<GatewayException>(() => command.ExecuteAsync(new(null, f.Inner.Registration, null, f.Route), default));
    }

    [TestMethod]
    public async Task InvalidRoutesModesStatesAndClaimsFailClosed()
    {
        AgentFollowUpFixture f = new(); await f.SeedAsync();
        foreach (StartAgentJobRequest request in new[]
        {
            f.Request with { ContinuationRef = null }, f.Request with { ContinuationRef = "https://untrusted.invalid" },
            f.Inner.Request with { ContinuationRef = f.Route.ContinuationRef }, f.Request with { CompletionMode = (JobCompletionMode)99 }
        }) { Assert.ThrowsExactly<JobException>(() => JobWire.CanonicalRequest(request, f.Inner.Gateway)); }
        f.Inner.Gateway.EnableParentContinuation = false;
        await Assert.ThrowsExactlyAsync<JobException>(() => f.Flow.StartAsync(f.Request, f.Inner.Owner, default));
        f.Inner.Gateway.EnableParentContinuation = true;
        f.Inner.Gateway.AllowConversationReuse = false;
        await Assert.ThrowsExactlyAsync<JobException>(() => f.Flow.StartAsync(f.Request, f.Inner.Owner, default));
        f.Inner.Gateway.AllowConversationReuse = true;
        AgentJob row = await f.StartAsync(true); AgentJobDomain domain = f.Inner.Domain(row.JobId);
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.ClaimObservationAsync("bad", f.Inner.Clock.Now, default));
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.ClaimContinuationAsync("bad", f.Inner.Clock.Now, 60, default));
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.ClaimContinuationAsync(Guid.NewGuid().ToString("N"), f.Inner.Clock.Now, 1, default));
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.RecordObservationAsync(row, new((JobProviderState)99), f.Inner.Clock.Now, default));
        Assert.ThrowsExactly<JobException>(() => (row with { ProviderStateCode = 99 }).RequireValid());
        Assert.ThrowsExactly<JobException>(() => (row with { ContinuationStateCode = 99 }).RequireValid());
        Assert.ThrowsExactly<JobException>(() => (row with { ParentApiChannelId = "https://untrusted.invalid" }).RequireValid());
        Assert.ThrowsExactly<JobException>(() => row.PrepareContinuation(1));
        Assert.ThrowsExactly<JobException>(() => f.Inner.Intent().PrepareContinuation(65536));
        Assert.ThrowsExactly<JobException>(() => JobFollowUp.ProviderName(99));
        Assert.ThrowsExactly<JobException>(() => JobFollowUp.ContinuationName(99));
        Assert.AreEqual("unrecognized", JobFollowUp.ProviderName((int)JobProviderState.Unrecognized));
        Assert.AreEqual("in_progress", JobFollowUp.ProviderName((int)JobProviderState.InProgress));
        f.Inner.Jobs.DropWrites = true;
        await Assert.ThrowsExactlyAsync<JobException>(() => domain.ClaimContinuationAsync(Guid.NewGuid().ToString("N"), f.Inner.Clock.Now, 60, default));
    }
}
