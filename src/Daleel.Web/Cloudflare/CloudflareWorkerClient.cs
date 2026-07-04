using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Daleel.Web.Storage;

namespace Daleel.Web.Cloudflare;

/// <summary>
/// The VPS side of the Elsa ↔ worker edge (doc §13.8): submit async jobs to the scrape-worker, check
/// their status, and read finished results back from R2 by key. Results travel by reference — the
/// worker writes R2 and this client reads R2 — so nothing round-trips through worker HTTP responses.
/// </summary>
public interface ICloudflareWorkerClient
{
    /// <summary>
    /// Submits a store-catalogue crawl (Context.dev on the edge) and returns its handle, or null when
    /// the worker rejected the submit or is unreachable — callers fall back to the inline path.
    /// <paramref name="maxProducts"/> ≤ 0 means uncapped (the vendor's own ceiling applies).
    /// </summary>
    Task<WorkerHandle?> SubmitCatalogAsync(
        string domain, string? store, string? searchJobId, int maxProducts = 0,
        CancellationToken ct = default);

    /// <summary>The worker's status for an async job, or null when it can't be reached.</summary>
    Task<WorkerJobStatus?> GetJobStatusAsync(string jobId, CancellationToken ct = default);

    /// <summary>Reads and deserializes a finished result from R2 by key; null when absent/unreadable.</summary>
    Task<T?> ReadResultAsync<T>(string resultKey, CancellationToken ct = default) where T : class;
}

/// <summary>Shared serializer settings for worker JSON (camelCase on the wire).</summary>
public static class CloudflareJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

public sealed class CloudflareWorkerClient : ICloudflareWorkerClient
{
    /// <summary>Catalogue results can be large (hundreds of products); read up to this many bytes.</summary>
    private const long MaxResultBytes = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly CloudflareWorkerOptions _options;
    private readonly IR2StorageService _r2;
    private readonly ILogger<CloudflareWorkerClient> _logger;

    public CloudflareWorkerClient(
        HttpClient http, CloudflareWorkerOptions options, IR2StorageService r2,
        ILogger<CloudflareWorkerClient> logger)
    {
        _http = http;
        _options = options;
        _r2 = r2;
        _logger = logger;
        _http.BaseAddress = options.ScrapeWorkerUrl;
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.ScrapeWorkerToken);
        // Submits and status checks are small control-plane calls; the heavy work runs on the edge.
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// A submit is called from inside a store sub-workflow whose whole budget is ~30s — a hanging
    /// worker must fail FAST into the inline fallback, not eat the child's budget and fault it.
    /// </summary>
    private static readonly TimeSpan SubmitTimeout = TimeSpan.FromSeconds(5);

    public async Task<WorkerHandle?> SubmitCatalogAsync(
        string domain, string? store, string? searchJobId, int maxProducts = 0,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["domain"] = domain,
            ["store"] = store,
            ["searchJobId"] = searchJobId,
            // Only send a cap when one was chosen — absent means "vendor ceiling" on the worker.
            ["maxProducts"] = maxProducts > 0 ? maxProducts : null
        };

        try
        {
            using var submitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            submitCts.CancelAfter(SubmitTimeout);
            using var response = await _http.PostAsync(
                "/scrape/catalog",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                submitCts.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(submitCts.Token).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<WorkerSubmitResponse>(text, CloudflareJson.Options);

            if (dto is not { Ok: true, JobId.Length: > 0, ResultKey.Length: > 0 })
            {
                _logger.LogWarning(
                    "scrape-worker rejected catalog submit for {Domain}: {Status} {Error}",
                    domain, (int)response.StatusCode, dto?.Error?.Message ?? Truncate(text));
                return null;
            }

            return new WorkerHandle { JobId = dto.JobId!, ResultKey = dto.ResultKey! };
        }
        // Only a genuine caller cancel propagates; a submit TIMEOUT falls through to the degrade
        // path below (the whole point of the short budget).
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Unreachable/slow worker must degrade to the inline path, never fault a search.
            _logger.LogWarning(ex, "scrape-worker catalog submit failed for {Domain}", domain);
            return null;
        }
    }

    public async Task<WorkerJobStatus?> GetJobStatusAsync(string jobId, CancellationToken ct = default)
    {
        try
        {
            using var statusCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            statusCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.GetAsync($"/jobs/{Uri.EscapeDataString(jobId)}", statusCts.Token)
                .ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(statusCts.Token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<WorkerJobStatus>(text, CloudflareJson.Options);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "scrape-worker status check failed for job {JobId}", jobId);
            return null;
        }
    }

    public async Task<T?> ReadResultAsync<T>(string resultKey, CancellationToken ct = default) where T : class
    {
        var obj = await _r2.ReadTextAsync(resultKey, MaxResultBytes, R2Bucket.Data, ct).ConfigureAwait(false);
        if (obj is null)
        {
            return null;
        }
        if (obj.Truncated)
        {
            // A clipped JSON document would deserialize to garbage — treat as unreadable and let the
            // caller keep polling/fault rather than persist a silently partial result.
            _logger.LogWarning("Worker result at {Key} exceeds {Max} bytes; refusing truncated read",
                resultKey, MaxResultBytes);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(obj.Text, CloudflareJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Worker result at {Key} is not valid {Type} JSON", resultKey, typeof(T).Name);
            return null;
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
