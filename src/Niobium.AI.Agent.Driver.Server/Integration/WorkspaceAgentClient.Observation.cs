using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Niobium.AI.Agent.Driver.Core;
using Niobium.AI.Agent.Driver.Core.Jobs;

namespace Niobium.AI.Agent.Driver.Server.Integration;

public sealed partial class WorkspaceAgentClient
{
    /// <summary>One bounded GET, no redirects or inherited retries. Unknown statuses are observation only.</summary>
    public async Task<ProviderObservation> ObserveAsync(string channel, string runId, string credential, CancellationToken cancellationToken)
    {
        if (!IdentifierRules.IsChannel(channel) || !JobFollowUp.IsRunId(runId) || !IsCredential(credential))
        { return new(null, ProviderObservationError.Unavailable, 3600); }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.Value.AttemptTimeoutSeconds));
        using HttpRequestMessage request = new(HttpMethod.Get,
            "https://api.chatgpt.com/v1/workspace_agents/" + channel + "/runs/" + runId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                TimeSpan pause = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is DateTimeOffset date ? date - clock.GetUtcNow() : TimeSpan.FromMinutes(1));
                return new(null, ProviderObservationError.RateLimited, (int)Math.Clamp(pause.TotalSeconds, 60, 3600));
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            { return new(null, ProviderObservationError.AccessDenied, 3600); }
            if (response.StatusCode != HttpStatusCode.OK) { return new(null, ProviderObservationError.Unavailable, 3600); }
            using JsonDocument document = await ReadBoundedJsonAsync(response.Content, timeout.Token);
            JsonElement root = document.RootElement;
            RequireObject(root);
            if (Text(root, "object") != "workspace_agent.trigger_run" || Text(root, "id") != runId || Text(root, "api_trigger_id") != channel)
            { return new(null, ProviderObservationError.InvalidResponse); }
            string? status = Text(root, "status");
            if (string.IsNullOrEmpty(status)) { return new(null, ProviderObservationError.InvalidResponse); }
            JobProviderState state = status switch
            {
                "queued" => JobProviderState.Queued, "in_progress" => JobProviderState.InProgress,
                "suspended" => JobProviderState.Suspended, "completed" => JobProviderState.Completed,
                "failed" => JobProviderState.Failed, _ => JobProviderState.Unrecognized
            };
            ProviderObservationError error = ProviderObservationError.None;
            if (state == JobProviderState.Failed)
            {
                string? code = root.TryGetProperty("error", out JsonElement details) && details.ValueKind == JsonValueKind.Object
                    ? Text(details, "code") : null;
                error = code switch
                { "dispatch_failed" => ProviderObservationError.DispatchFailed, "run_failed" => ProviderObservationError.RunFailed,
                    _ => ProviderObservationError.Unavailable };
            }
            return new(state, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(null, ProviderObservationError.DeadlineExceeded); }
        catch (HttpRequestException) { return new(null, ProviderObservationError.Unavailable); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        { return new(null, ProviderObservationError.InvalidResponse); }
    }

    private async Task<JsonDocument> ReadBoundedJsonAsync(HttpContent content, CancellationToken token)
    {
        int limit = Math.Clamp(settings.Value.MaxProviderBodyBytes, 1024, 65536);
        if (content.Headers.ContentLength > limit) { throw new JsonException("Metadata exceeds the safe limit."); }
        await using Stream stream = await content.ReadAsStreamAsync(token);
        byte[] bytes = new byte[limit + 1];
        int length = 0;
        while (length < bytes.Length)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(length), token);
            if (read == 0) { return JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 16 }); }
            length += read;
        }
        throw new JsonException("Metadata exceeds the safe limit.");
    }

    private static void RequireObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()
            != root.EnumerateObject().Count()) { throw new JsonException("Invalid metadata object."); }
    }

    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
