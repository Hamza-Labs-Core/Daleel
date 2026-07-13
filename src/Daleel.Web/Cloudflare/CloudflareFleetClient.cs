using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daleel.Web.Cloudflare;

/// <summary>
/// Typed client for the Workers-AI fleet hosts (classify / extract / filter — doc §3.2–3.4). All
/// calls are best-effort signals: a null/empty return means "no verdict", and callers keep their
/// current inline behavior. Reached ONLY through <see cref="Services.IProviderApi"/> so every call
/// is metered; nothing else should hold this interface.
/// </summary>
/// <remarks>
/// Deliberately capability-shaped, not vendor-shaped: the filter host returns FINDINGS the VPS
/// <c>HalalModerator</c> may weigh — policy (whitelist, thresholds, riba-never-filtered, veto,
/// audit) never leaves the VPS, and these signals must be A/B-validated against the current
/// classifier before any default routing flips (doc §6 Phase 3).
/// </remarks>
public interface ICloudflareFleetClient
{
    bool HasClassify { get; }
    bool HasExtract { get; }
    bool HasFilter { get; }

    /// <summary>Labels each item with one of the given labels; empty on failure/unconfigured.</summary>
    Task<IReadOnlyList<ClassifyVerdict>> ClassifyTextAsync(
        IReadOnlyList<(string Id, string Text)> items, IReadOnlyList<string> labels, CancellationToken ct = default);

    /// <summary>Extracts catalogue-shaped products from page content; empty on failure/unconfigured.</summary>
    Task<IReadOnlyList<Daleel.Search.Providers.CatalogProduct>> ExtractProductsAsync(
        string content, string? market = null, CancellationToken ct = default);

    /// <summary>Halal findings (signals only) for a batch of texts; empty on failure/unconfigured.</summary>
    Task<IReadOnlyList<FilterFindingDto>> FilterTextAsync(
        IReadOnlyList<(string Id, string Text, string? SourceUrl)> items, CancellationToken ct = default);

    /// <summary>Halal findings (signals only) for a batch of image urls; empty on failure/unconfigured.</summary>
    Task<IReadOnlyList<FilterFindingDto>> FilterImagesAsync(
        IReadOnlyList<string> urls, CancellationToken ct = default);
}

public sealed record ClassifyVerdict
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("confidence")] public double Confidence { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

public sealed record FilterFindingDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("category")] public string? Category { get; init; }
    [JsonPropertyName("confidence")] public double Confidence { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }
}

public sealed class CloudflareFleetClient : ICloudflareFleetClient
{
    private readonly HttpClient? _classify;
    private readonly HttpClient? _extract;
    private readonly HttpClient? _filter;
    private readonly ILogger<CloudflareFleetClient> _logger;

    private readonly Func<string, string?>? _bearer;

    public CloudflareFleetClient(
        CloudflareFleetOptions options, Func<HttpClient> clientFactory, ILogger<CloudflareFleetClient> logger,
        Func<string, string?>? bearer = null)
    {
        _logger = logger;
        // Per-capability bearer provider ("classify" | "extract" | "filter") — vault snapshot first,
        // so a rotated token applies to these long-lived clients without a restart; the endpoint's
        // env-configured token stays as the DefaultRequestHeaders fallback.
        _bearer = bearer;
        _classify = Build(options.Classify, clientFactory);
        _extract = Build(options.Extract, clientFactory);
        _filter = Build(options.Filter, clientFactory);
    }

    private static HttpClient? Build(WorkerEndpoint? endpoint, Func<HttpClient> factory)
    {
        if (endpoint is null)
        {
            return null;
        }

        var client = factory();
        client.BaseAddress = endpoint.BaseUrl;
        // Static fallback only — the per-request vault bearer overrides this; both may be absent
        // briefly at boot (before the vault snapshot loads), in which case the worker 401s and the
        // caller keeps its inline path.
        if (!string.IsNullOrWhiteSpace(endpoint.Token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        }
        // Workers-AI inference is seconds, not minutes; a hung call must degrade, never wedge a phase.
        client.Timeout = TimeSpan.FromSeconds(60);
        return client;
    }

    public bool HasClassify => _classify is not null;
    public bool HasExtract => _extract is not null;
    public bool HasFilter => _filter is not null;

    // Mirrors MAX_TEXT_ITEMS in workers/classify-worker/src/index.js — the worker 400s any larger
    // batch, so both constants must move together.
    private const int MaxClassifyTextItemsPerCall = 100;

    public async Task<IReadOnlyList<ClassifyVerdict>> ClassifyTextAsync(
        IReadOnlyList<(string Id, string Text)> items, IReadOnlyList<string> labels, CancellationToken ct = default)
    {
        var verdicts = await PostChunkedAsync(items, MaxClassifyTextItemsPerCall, chunk =>
            PostAsync<ClassifyResponse>(_classify, "classify", "/classify/text", new
            {
                items = chunk.Select(i => new { id = i.Id, text = i.Text }),
                labels
            }, ct), r => r.Verdicts).ConfigureAwait(false);
        return verdicts ?? (IReadOnlyList<ClassifyVerdict>)Array.Empty<ClassifyVerdict>();
    }

    public async Task<IReadOnlyList<Daleel.Search.Providers.CatalogProduct>> ExtractProductsAsync(
        string content, string? market = null, CancellationToken ct = default)
    {
        var result = await PostAsync<ExtractResponse>(_extract, "extract", "/extract/products", new
        {
            content,
            market
        }, ct).ConfigureAwait(false);
        return result?.Products ?? (IReadOnlyList<Daleel.Search.Providers.CatalogProduct>)Array.Empty<Daleel.Search.Providers.CatalogProduct>();
    }

    // Mirrors MAX_TEXT_ITEMS in workers/filter-worker/src/index.js — the worker 400s any larger
    // batch (QA job #48 lost a 59-item halal batch that way), so both constants must move together.
    private const int MaxFilterTextItemsPerCall = 50;

    public async Task<IReadOnlyList<FilterFindingDto>> FilterTextAsync(
        IReadOnlyList<(string Id, string Text, string? SourceUrl)> items, CancellationToken ct = default)
    {
        var findings = await PostChunkedAsync(items, MaxFilterTextItemsPerCall, chunk =>
            PostAsync<FilterResponse>(_filter, "filter", "/filter/text", new
            {
                items = chunk.Select(i => new { id = i.Id, text = i.Text, sourceUrl = i.SourceUrl })
            }, ct), r => r.Findings).ConfigureAwait(false);
        return findings ?? (IReadOnlyList<FilterFindingDto>)Array.Empty<FilterFindingDto>();
    }

    // Mirrors MAX_IMAGE_URLS in workers/filter-worker/src/index.js — the worker 400s any larger
    // batch, so both constants must move together.
    private const int MaxImageUrlsPerCall = 20;

    public async Task<IReadOnlyList<FilterFindingDto>> FilterImagesAsync(
        IReadOnlyList<string> urls, CancellationToken ct = default)
    {
        var findings = await PostChunkedAsync(urls, MaxImageUrlsPerCall, chunk =>
            PostAsync<FilterResponse>(_filter, "filter", "/filter/images", new { urls = chunk }, ct),
            r => r.Findings).ConfigureAwait(false);
        return findings ?? (IReadOnlyList<FilterFindingDto>)Array.Empty<FilterFindingDto>();
    }

    /// <summary>
    /// Splits a batch into worker-cap-sized chunks and merges results in input order. Every fleet
    /// host hard-400s oversized batches, so the cap is enforced HERE — callers batch freely (the
    /// halal shadow sends whole moderation runs) and must never learn per-worker limits. Chunks go
    /// out sequentially, and a failed chunk loses only its own results (best-effort contract).
    /// </summary>
    private static async Task<List<TResult>?> PostChunkedAsync<TItem, TResponse, TResult>(
        IReadOnlyList<TItem> items, int maxPerCall, Func<TItem[], Task<TResponse?>> post,
        Func<TResponse, List<TResult>?> select)
        where TResponse : class
    {
        List<TResult>? merged = null;
        for (var offset = 0; offset < items.Count; offset += maxPerCall)
        {
            var chunk = items.Skip(offset).Take(maxPerCall).ToArray();
            var response = await post(chunk).ConfigureAwait(false);
            if (response is not null && select(response) is { Count: > 0 } results)
            {
                (merged ??= new List<TResult>()).AddRange(results);
            }
        }

        return merged;
    }

    /// <summary>POSTs to a fleet host and unwraps the {ok, result} envelope; null on any failure.</summary>
    private async Task<T?> PostAsync<T>(
        HttpClient? client, string capability, string path, object body, CancellationToken ct)
        where T : class
    {
        if (client is null)
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            if (_bearer?.Invoke(capability) is { } token)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<Envelope<T>>(text, CloudflareJson.Options);
            if (envelope is not { Ok: true })
            {
                _logger.LogWarning("Fleet call {Path} failed: {Status} {Error}",
                    path, (int)response.StatusCode, envelope?.Error?.Message);
                return null;
            }

            // Result may be wrapped ({result: {...}}) or inline at the root, mirroring the workers.
            return envelope.Result ?? JsonSerializer.Deserialize<T>(text, CloudflareJson.Options);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Fleet signals are best-effort by contract — degrade, never fault the caller.
            _logger.LogWarning(ex, "Fleet call {Path} failed", path);
            return null;
        }
    }

    private sealed record Envelope<T>
    {
        [JsonPropertyName("ok")] public bool Ok { get; init; }
        [JsonPropertyName("result")] public T? Result { get; init; }
        [JsonPropertyName("error")] public WorkerError? Error { get; init; }
    }

    private sealed record ClassifyResponse
    {
        [JsonPropertyName("verdicts")] public List<ClassifyVerdict>? Verdicts { get; init; }
    }

    private sealed record ExtractResponse
    {
        [JsonPropertyName("products")] public List<Daleel.Search.Providers.CatalogProduct>? Products { get; init; }
    }

    private sealed record FilterResponse
    {
        [JsonPropertyName("findings")] public List<FilterFindingDto>? Findings { get; init; }
    }
}
