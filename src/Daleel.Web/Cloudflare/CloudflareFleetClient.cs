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
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        // Workers-AI inference is seconds, not minutes; a hung call must degrade, never wedge a phase.
        client.Timeout = TimeSpan.FromSeconds(60);
        return client;
    }

    public bool HasClassify => _classify is not null;
    public bool HasExtract => _extract is not null;
    public bool HasFilter => _filter is not null;

    public async Task<IReadOnlyList<ClassifyVerdict>> ClassifyTextAsync(
        IReadOnlyList<(string Id, string Text)> items, IReadOnlyList<string> labels, CancellationToken ct = default)
    {
        var result = await PostAsync<ClassifyResponse>(_classify, "classify", "/classify/text", new
        {
            items = items.Select(i => new { id = i.Id, text = i.Text }),
            labels
        }, ct).ConfigureAwait(false);
        return result?.Verdicts ?? (IReadOnlyList<ClassifyVerdict>)Array.Empty<ClassifyVerdict>();
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

    public async Task<IReadOnlyList<FilterFindingDto>> FilterTextAsync(
        IReadOnlyList<(string Id, string Text, string? SourceUrl)> items, CancellationToken ct = default)
    {
        var result = await PostAsync<FilterResponse>(_filter, "filter", "/filter/text", new
        {
            items = items.Select(i => new { id = i.Id, text = i.Text, sourceUrl = i.SourceUrl })
        }, ct).ConfigureAwait(false);
        return result?.Findings ?? (IReadOnlyList<FilterFindingDto>)Array.Empty<FilterFindingDto>();
    }

    public async Task<IReadOnlyList<FilterFindingDto>> FilterImagesAsync(
        IReadOnlyList<string> urls, CancellationToken ct = default)
    {
        var result = await PostAsync<FilterResponse>(_filter, "filter", "/filter/images", new { urls }, ct)
            .ConfigureAwait(false);
        return result?.Findings ?? (IReadOnlyList<FilterFindingDto>)Array.Empty<FilterFindingDto>();
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
