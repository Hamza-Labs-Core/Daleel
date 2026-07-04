using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daleel.Web.Cloudflare;

/// <summary>
/// Cloudflare Queues <em>pull HTTP consumer</em> (doc §4.2/§13.9): the VPS leases batches of poll
/// messages, acks the ones whose results it persisted, and re-queues still-pending ones with an explicit
/// <c>delay_seconds</c> backoff. Pull (not push) because the thing reacting is Elsa on the VPS, which a
/// push consumer (a Worker) could never be.
/// </summary>
public interface IQueuePullClient
{
    /// <summary>True when account/queue/token are all configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Leases up to <paramref name="batchSize"/> messages; empty on any failure (best-effort).</summary>
    Task<IReadOnlyList<PulledMessage>> PullAsync(
        int batchSize = 10, int visibilityTimeoutMs = 60_000, CancellationToken ct = default);

    /// <summary>Acks completed leases and re-queues pending ones with a per-lease delay.</summary>
    Task AckAsync(
        IReadOnlyList<string> ackLeaseIds,
        IReadOnlyList<(string LeaseId, int DelaySeconds)> retries,
        CancellationToken ct = default);
}

public sealed class QueuePullClient : IQueuePullClient
{
    private readonly HttpClient _http;
    private readonly CloudflareWorkerOptions _options;
    private readonly ILogger<QueuePullClient> _logger;

    public QueuePullClient(HttpClient http, CloudflareWorkerOptions options, ILogger<QueuePullClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
        _http.BaseAddress = new Uri("https://api.cloudflare.com/");
        if (!string.IsNullOrWhiteSpace(options.QueuesApiToken))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.QueuesApiToken);
        }
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public bool IsConfigured => _options.CanDrainQueue;

    public async Task<IReadOnlyList<PulledMessage>> PullAsync(
        int batchSize = 10, int visibilityTimeoutMs = 60_000, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return Array.Empty<PulledMessage>();
        }

        try
        {
            using var response = await _http.PostAsync(
                PullPath(),
                JsonBody(new { batch_size = batchSize, visibility_timeout_ms = visibilityTimeoutMs }),
                ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Queue pull failed: {Status} {Body}", (int)response.StatusCode, Clip(text));
                return Array.Empty<PulledMessage>();
            }

            var dto = JsonSerializer.Deserialize<PullResponse>(text, CloudflareJson.Options);
            return dto?.Result?.Messages ?? (IReadOnlyList<PulledMessage>)Array.Empty<PulledMessage>();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // The drain loop ticks again shortly; a failed pull is noise, not an incident.
            _logger.LogWarning(ex, "Queue pull failed");
            return Array.Empty<PulledMessage>();
        }
    }

    public async Task AckAsync(
        IReadOnlyList<string> ackLeaseIds,
        IReadOnlyList<(string LeaseId, int DelaySeconds)> retries,
        CancellationToken ct = default)
    {
        if (!IsConfigured || (ackLeaseIds.Count == 0 && retries.Count == 0))
        {
            return;
        }

        try
        {
            var body = new
            {
                acks = ackLeaseIds.Select(l => new { lease_id = l }).ToArray(),
                retries = retries.Select(r => new { lease_id = r.LeaseId, delay_seconds = r.DelaySeconds }).ToArray()
            };
            using var response = await _http.PostAsync(AckPath(), JsonBody(body), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // At-least-once delivery makes a failed ack safe: the message reappears after the
                // visibility timeout and the (idempotent) handler re-runs.
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogWarning("Queue ack failed: {Status} {Body}", (int)response.StatusCode, Clip(text));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Queue ack failed");
        }
    }

    private string PullPath() =>
        $"client/v4/accounts/{_options.AccountId}/queues/{_options.PollQueueId}/messages/pull";

    private string AckPath() =>
        $"client/v4/accounts/{_options.AccountId}/queues/{_options.PollQueueId}/messages/ack";

    private static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static string Clip(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private sealed record PullResponse
    {
        [JsonPropertyName("result")] public PullResult? Result { get; init; }
    }

    private sealed record PullResult
    {
        [JsonPropertyName("messages")] public List<PulledMessage>? Messages { get; init; }
    }
}
