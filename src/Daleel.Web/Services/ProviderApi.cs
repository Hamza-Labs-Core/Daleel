using Daleel.Apify;
using Daleel.Core.Geo;
using Daleel.Core.Models;
using Daleel.Core.Observability;
using Daleel.Search.Abstractions;
using Daleel.Search.Providers;
using Daleel.Web.Cloudflare;

namespace Daleel.Web.Services;

/// <summary>
/// THE internal API for provider calls made outside an <c>AgentService</c> (whose own providers are
/// wrapped by <c>LoggingProviders</c> at build time). Pipeline activities and background services call
/// providers ONLY through this gateway — never by constructing a provider directly — so every external
/// call is metered by construction: timed, cost-estimated, counted toward the per-job cap, and
/// persisted to the usage log via the ambient <see cref="AmbientApiObserver"/> of whatever job is
/// running on the current async flow.
/// </summary>
/// <remarks>
/// This closes the metering leaks the ad-hoc pattern invited (each call site had to remember its own
/// <see cref="ApiCallTimer"/> wrap — the inline store-catalogue crawl didn't, and its spend was
/// invisible). Edge submits are metered here too, at submit time, with the same per-call estimate the
/// inline path uses — so the live cost cap sees edge work identically and nothing double-counts.
/// New provider capabilities belong HERE, not at call sites.
/// </remarks>
public interface IProviderApi
{
    /// <summary>True when a scraping backend (Context.dev) is configured.</summary>
    bool HasScraper { get; }

    /// <summary>True when the Cloudflare execution layer is registered (edge submits possible).</summary>
    bool HasEdge { get; }

    /// <summary>
    /// True when the FULL edge return path exists (poll-queue credentials + R2): a submit replaces
    /// inline persistence, so handing work off without a drain would strand results permanently.
    /// </summary>
    bool EdgeDrainReady { get; }

    /// <summary>A store/brand catalogue with pricing. maxProducts ≤ 0 ⇒ uncapped (vendor ceiling).</summary>
    Task<IReadOnlyList<CatalogProduct>> ExtractCatalogAsync(
        string domain, int maxProducts = 0, int timeoutMs = 45_000, CancellationToken ct = default);

    /// <summary>Brand intelligence for a domain, or null when unconfigured/failed.</summary>
    Task<BrandProfile?> GetBrandAsync(string domain, CancellationToken ct = default);

    /// <summary>One page rendered to markdown/HTML, or null when unconfigured/failed-empty.</summary>
    Task<ScrapedPage?> ScrapePageAsync(
        string url, ScrapeFormat format = ScrapeFormat.Markdown, CancellationToken ct = default);

    /// <summary>True when a Places backend is configured.</summary>
    bool HasPlaces { get; }

    /// <summary>Google Places text search near a point; empty when unconfigured/failed.</summary>
    Task<IReadOnlyList<StoreLocation>> SearchPlacesAsync(
        string query, GeoPoint? near = null, double radiusMeters = 5000, string? languageCode = null,
        CancellationToken ct = default);

    /// <summary>Full place details (hours/reviews field mask); null when unconfigured/failed.</summary>
    Task<StoreLocation?> GetPlaceDetailsAsync(string placeId, CancellationToken ct = default);

    /// <summary>True when the social fetcher (Apify) is configured.</summary>
    bool HasSocial { get; }

    /// <summary>Social posts for a source/keyword; empty when unconfigured/failed.</summary>
    Task<IReadOnlyList<SocialPost>> FetchSocialPostsAsync(
        Source source, string? keyword = null, CancellationToken ct = default);

    /// <summary>
    /// Submits a catalogue crawl to the edge scrape-worker, metering the submit with the same
    /// per-call estimate an inline crawl would record (the vendor work happens on the edge, but the
    /// spend belongs to THIS job). Null when the edge is unconfigured or the submit failed.
    /// </summary>
    Task<WorkerHandle?> SubmitEdgeCatalogAsync(
        string domain, string? store, string? searchJobId, int maxProducts = 0, CancellationToken ct = default);

    /// <summary>
    /// Submits a BRAND crawl (profile + catalogue) to the edge scrape-worker; the drain persists the
    /// harvested models into the brand-model DB when the result lands. Same metering/fallback
    /// contract as <see cref="SubmitEdgeCatalogAsync"/>.
    /// </summary>
    Task<WorkerHandle?> SubmitEdgeBrandAsync(
        string domain, string brandName, string? searchJobId, CancellationToken ct = default);

    /// <summary>True when the edge extract host is configured.</summary>
    bool HasEdgeExtract { get; }

    // ── Workers-AI fleet (doc §3.2–3.4) — signals only, metered, best-effort ────────────────────
    // Callers own thresholds/policy; empty results mean "no verdict, keep the inline behavior".
    // Filter findings in particular feed the VPS HalalModerator and must be A/B-validated before
    // any default routing flips (doc §6 Phase 3).

    /// <summary>True when the classify host is configured.</summary>
    bool HasEdgeClassify { get; }

    /// <summary>True when the filter host is configured.</summary>
    bool HasEdgeFilter { get; }

    /// <summary>Commodity text labeling on the edge; empty on failure/unconfigured.</summary>
    Task<IReadOnlyList<Cloudflare.ClassifyVerdict>> ClassifyTextAsync(
        IReadOnlyList<(string Id, string Text)> items, IReadOnlyList<string> labels, CancellationToken ct = default);

    /// <summary>Workers-AI structured extraction from page content; empty on failure/unconfigured.</summary>
    Task<IReadOnlyList<CatalogProduct>> ExtractProductsFromContentAsync(
        string content, string? market = null, CancellationToken ct = default);

    /// <summary>Halal findings (signals only) for texts; empty on failure/unconfigured.</summary>
    Task<IReadOnlyList<Cloudflare.FilterFindingDto>> FilterTextFindingsAsync(
        IReadOnlyList<(string Id, string Text, string? SourceUrl)> items, CancellationToken ct = default);

    /// <summary>Halal findings (signals only) for image urls; empty on failure/unconfigured.</summary>
    Task<IReadOnlyList<Cloudflare.FilterFindingDto>> FilterImageFindingsAsync(
        IReadOnlyList<string> urls, CancellationToken ct = default);
}

public sealed class ProviderApi : IProviderApi
{
    private readonly IAgentFactory _factory;
    private readonly ICloudflareWorkerClient? _edge;
    private readonly ICloudflareFleetClient? _fleet;
    private readonly object _gate = new();
    private ContextDevProvider? _contextDev;
    private string? _contextDevKey;
    private GooglePlacesProvider? _places;
    private string? _placesKey;
    private ApifyPostFetcher? _social;
    private string? _socialToken;

    private readonly CloudflareWorkerOptions? _edgeOptions;
    private readonly Daleel.Web.Storage.IR2StorageService? _r2;

    public ProviderApi(
        IAgentFactory factory, ICloudflareWorkerClient? edge = null, ICloudflareFleetClient? fleet = null,
        CloudflareWorkerOptions? edgeOptions = null, Daleel.Web.Storage.IR2StorageService? r2 = null)
    {
        _factory = factory;
        _edge = edge;
        _fleet = fleet;
        _edgeOptions = edgeOptions;
        _r2 = r2;
    }

    public bool HasScraper => _factory.Resolve("CONTEXT_DEV_API_KEY") is not null;

    public bool HasEdge => _edge is not null;

    public bool EdgeDrainReady =>
        _edge is not null && _edgeOptions is { CanDrainQueue: true } && _r2 is { IsConfigured: true };

    public async Task<IReadOnlyList<CatalogProduct>> ExtractCatalogAsync(
        string domain, int maxProducts = 0, int timeoutMs = 45_000, CancellationToken ct = default)
    {
        if (ContextDev() is not { } ctx)
        {
            return Array.Empty<CatalogProduct>();
        }

        // No bytes selector: a product COUNT must not masquerade as ResponseBytes in the usage log.
        return await MeterAsync(
            "Context.dev", "catalog/extract", domain,
            () => ctx.ExtractProductsAsync(domain, maxProducts, timeoutMs, ct)).ConfigureAwait(false);
    }

    public async Task<BrandProfile?> GetBrandAsync(string domain, CancellationToken ct = default)
    {
        if (ContextDev() is not { } ctx)
        {
            return null;
        }

        return await MeterAsync(
            "Context.dev", "brand/retrieve", domain,
            () => ctx.GetBrandAsync(domain, ct)).ConfigureAwait(false);
    }

    public async Task<ScrapedPage?> ScrapePageAsync(
        string url, ScrapeFormat format = ScrapeFormat.Markdown, CancellationToken ct = default)
    {
        // Edge first when available: same vendor call from the worker (key lives on the edge),
        // metered identically; any edge failure degrades to the inline provider below.
        if (_edge is not null)
        {
            var edgePage = await MeterAsync(
                "scrape-worker/context.dev", $"scrape/{format.ToString().ToLowerInvariant()}", url,
                () => _edge.ScrapePageAsync(url, format, ct),
                p => p?.Content?.Length ?? 0,
                // Bill the edge attempt ONLY when it delivered; a null/failed edge page must cost
                // nothing so the inline fallback below is the single charge (no double-bill on a
                // worker outage or bearer rotation).
                success: p => p is { Success: true }).ConfigureAwait(false);
            if (edgePage is { Success: true })
            {
                return edgePage;
            }
        }

        if (ContextDev() is not { } ctx)
        {
            return null;
        }

        var page = await MeterAsync(
            "Context.dev", $"scrape/{format.ToString().ToLowerInvariant()}", url,
            () => ctx.ScrapeAsync(url, format, ct),
            p => p.Content?.Length ?? 0).ConfigureAwait(false);
        return page.Success ? page : null;
    }

    public async Task<WorkerHandle?> SubmitEdgeCatalogAsync(
        string domain, string? store, string? searchJobId, int maxProducts = 0, CancellationToken ct = default)
    {
        if (_edge is null)
        {
            return null;
        }

        // Metered as the crawl it triggers (the "context.dev"-family name keeps the vendor extract
        // rate; the "-worker/" name adds the pricing.edge_request hop on top), so edge and inline
        // crawls hit the cap and the usage log identically. Recorded ONLY on an accepted submit: a
        // failed/rejected submit costs nothing and falls back to the inline path, whose own metering
        // then records the crawl — recording here too would double-charge the same crawl. The drain
        // meters only its own queue+R2 overhead ("cloudflare/drain"); this call stays the crawl's
        // whole vendor accounting.
        var started = System.Diagnostics.Stopwatch.StartNew();
        var handle = await _edge.SubmitCatalogAsync(domain, store, searchJobId, maxProducts, ct).ConfigureAwait(false);
        if (handle is not null && AmbientApiObserver.Observer is { } observer)
        {
            var estimator = AmbientApiObserver.Estimator ?? new CostEstimator();
            observer.Record(new ApiCall
            {
                Timestamp = DateTimeOffset.UtcNow,
                Provider = "scrape-worker/context.dev",
                Endpoint = "catalog/extract",
                RequestSummary = domain,
                ResponseTimeMs = started.ElapsedMilliseconds,
                Status = ApiCallStatus.Success,
                EstimatedCost = estimator.EstimateCall("scrape-worker/context.dev", "catalog/extract")
            });
        }
        return handle;
    }

    public bool HasPlaces => _factory.Resolve("GOOGLE_PLACES_API_KEY") is not null || HasSearchProxy;

    public async Task<IReadOnlyList<StoreLocation>> SearchPlacesAsync(
        string query, GeoPoint? near = null, double radiusMeters = 5000, string? languageCode = null,
        CancellationToken ct = default)
    {
        if (Places() is not { } places)
        {
            return Array.Empty<StoreLocation>();
        }

        return await MeterAsync(
            PlacesProviderName, "places/text-search", query,
            () => places.SearchStoresAsync(query, near, radiusMeters, languageCode, ct),
            r => r.Count).ConfigureAwait(false);
    }

    public async Task<StoreLocation?> GetPlaceDetailsAsync(string placeId, CancellationToken ct = default)
    {
        if (Places() is not { } places)
        {
            return null;
        }

        return await MeterAsync(
            PlacesProviderName, "places/details", placeId,
            () => places.GetPlaceDetailsAsync(placeId, ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Proxied Places calls are named for the worker hop they ride ("search-worker/…") so the
    /// estimator adds pricing.edge_request on top of the Places rate and the usage log shows the
    /// route; the name still says "places", so user credits match a direct call exactly.
    /// </summary>
    private string PlacesProviderName =>
        _factory.Resolve("GOOGLE_PLACES_API_KEY") is null && HasSearchProxy
            ? "search-worker/google-places"
            : "Google Places";

    public bool HasSocial => _factory.Resolve("APIFY_TOKEN") is not null;

    public async Task<IReadOnlyList<SocialPost>> FetchSocialPostsAsync(
        Source source, string? keyword = null, CancellationToken ct = default)
    {
        if (Social() is not { } social)
        {
            return Array.Empty<SocialPost>();
        }

        return await MeterAsync(
            "Apify", "social/fetch", keyword ?? source.Target,
            () => social.FetchAsync(source, keyword, ct),
            r => r.Count).ConfigureAwait(false);
    }

    public bool HasEdgeExtract => _fleet?.HasExtract ?? false;

    public bool HasEdgeClassify => _fleet?.HasClassify ?? false;

    public bool HasEdgeFilter => _fleet?.HasFilter ?? false;

    public async Task<IReadOnlyList<Cloudflare.ClassifyVerdict>> ClassifyTextAsync(
        IReadOnlyList<(string Id, string Text)> items, IReadOnlyList<string> labels, CancellationToken ct = default)
    {
        if (_fleet is not { HasClassify: true })
        {
            return Array.Empty<Cloudflare.ClassifyVerdict>();
        }

        return await MeterAsync(
            "workers-ai/classify", "classify/text", $"{items.Count} item(s)",
            () => _fleet.ClassifyTextAsync(items, labels, ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CatalogProduct>> ExtractProductsFromContentAsync(
        string content, string? market = null, CancellationToken ct = default)
    {
        if (_fleet is not { HasExtract: true })
        {
            return Array.Empty<CatalogProduct>();
        }

        return await MeterAsync(
            "workers-ai/extract", "extract/products", $"{content.Length} chars",
            () => _fleet.ExtractProductsAsync(content, market, ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Cloudflare.FilterFindingDto>> FilterTextFindingsAsync(
        IReadOnlyList<(string Id, string Text, string? SourceUrl)> items, CancellationToken ct = default)
    {
        if (_fleet is not { HasFilter: true })
        {
            return Array.Empty<Cloudflare.FilterFindingDto>();
        }

        return await MeterAsync(
            "workers-ai/filter", "filter/text", $"{items.Count} item(s)",
            () => _fleet.FilterTextAsync(items, ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Cloudflare.FilterFindingDto>> FilterImageFindingsAsync(
        IReadOnlyList<string> urls, CancellationToken ct = default)
    {
        if (_fleet is not { HasFilter: true })
        {
            return Array.Empty<Cloudflare.FilterFindingDto>();
        }

        return await MeterAsync(
            "workers-ai/filter", "filter/images", $"{urls.Count} url(s)",
            () => _fleet.FilterImagesAsync(urls, ct)).ConfigureAwait(false);
    }

    private bool HasSearchProxy =>
        _factory.Resolve("CF_SEARCH_WORKER_URL") is not null &&
        _factory.Resolve("CF_SEARCH_WORKER_TOKEN") is not null;

    /// <summary>
    /// A fresh HttpClient pointed at the search-worker proxy (bearer preset), or null — the same
    /// routing AgentFactory gives the agent's own providers, so gateway Places calls ride the edge
    /// cache and key relocation identically.
    /// </summary>
    private HttpClient? SearchProxyClient()
    {
        if (_factory.Resolve("CF_SEARCH_WORKER_URL") is not { } url ||
            _factory.Resolve("CF_SEARCH_WORKER_TOKEN") is not { } token ||
            !Uri.TryCreate(url, UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var client = Daleel.Search.Http.SharedHttpHandler.CreateClient();
        client.BaseAddress = baseUri;
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Cached Places provider (rebuilt when the resolved key OR — when proxied — the proxy bearer
    /// changes); proxy-aware. Internal so a test can prove the proxied client rebuilds on rotation.
    /// </summary>
    internal GooglePlacesProvider? Places()
    {
        var key = _factory.Resolve("GOOGLE_PLACES_API_KEY");
        var proxied = key is null && HasSearchProxy;
        if (key is null && !proxied)
        {
            return null;
        }

        // When proxied, SearchProxyClient bakes the current CF_SEARCH_WORKER_TOKEN bearer into its
        // DefaultRequestHeaders — so the cache key MUST include that bearer, or a rotated token leaves
        // the cached client presenting a stale value the worker rejects (401) until process restart.
        // A constant "edge-proxied" key would never rebuild; keying by the live bearer rebuilds on
        // rotation. The provider still receives the plain "edge-proxied" placeholder key (the real
        // auth rides the proxy client's header, not the Places X-Goog-Api-Key).
        var effectiveKey = key ?? $"edge-proxied:{_factory.Resolve("CF_SEARCH_WORKER_TOKEN") ?? ""}";
        lock (_gate)
        {
            if (_places is null || !string.Equals(_placesKey, effectiveKey, StringComparison.Ordinal))
            {
                _places = new GooglePlacesProvider(key ?? "edge-proxied", SearchProxyClient());
                _placesKey = effectiveKey;
            }
            return _places;
        }
    }

    /// <summary>Cached social fetcher (rebuilt only if the resolved token changes).</summary>
    private ApifyPostFetcher? Social()
    {
        var token = _factory.Resolve("APIFY_TOKEN");
        if (token is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_social is null || !string.Equals(_socialToken, token, StringComparison.Ordinal))
            {
                _social = new ApifyPostFetcher(new ApifyClient(token));
                _socialToken = token;
            }
            return _social;
        }
    }

    public async Task<WorkerHandle?> SubmitEdgeBrandAsync(
        string domain, string brandName, string? searchJobId, CancellationToken ct = default)
    {
        if (_edge is null)
        {
            return null;
        }

        // Same accounting contract as the catalogue submit: recorded only on an accepted 202 (a
        // failed submit costs nothing and the inline harvest's own metering takes over). Estimated
        // at the brand-lookup rate + the catalogue crawl it triggers is covered by the same call.
        var started = System.Diagnostics.Stopwatch.StartNew();
        var handle = await _edge.SubmitBrandAsync(domain, brandName, searchJobId, ct).ConfigureAwait(false);
        if (handle is not null && AmbientApiObserver.Observer is { } observer)
        {
            var estimator = AmbientApiObserver.Estimator ?? new CostEstimator();
            observer.Record(new ApiCall
            {
                Timestamp = DateTimeOffset.UtcNow,
                Provider = "scrape-worker/context.dev",
                Endpoint = "brand/ai/products",
                RequestSummary = domain,
                ResponseTimeMs = started.ElapsedMilliseconds,
                Status = ApiCallStatus.Success,
                EstimatedCost = estimator.EstimateCall("scrape-worker/context.dev", "brand/ai/products")
            });
        }
        return handle;
    }

    /// <summary>Cached Context.dev provider (rebuilt only if the resolved key changes).</summary>
    private ContextDevProvider? ContextDev()
    {
        var key = _factory.Resolve("CONTEXT_DEV_API_KEY");
        if (key is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_contextDev is null || !string.Equals(_contextDevKey, key, StringComparison.Ordinal))
            {
                _contextDev = new ContextDevProvider(key);
                _contextDevKey = key;
            }
            return _contextDev;
        }
    }

    /// <summary>
    /// Every gateway call funnels through here: timed + cost-estimated against the AMBIENT observer,
    /// i.e. whatever job's <see cref="JobApiCallCollector"/> is active on this async flow. Off-job
    /// calls (no ambient observer) still run — they're simply not attributed.
    /// </summary>
    private static Task<T> MeterAsync<T>(
        string provider, string endpoint, string? summary, Func<Task<T>> action,
        Func<T, long>? bytes = null, Func<T, bool>? success = null) =>
        ApiCallTimer.TimeAsync(
            AmbientApiObserver.Observer,
            AmbientApiObserver.Estimator ?? new CostEstimator(),
            provider, endpoint, summary, action, bytes, success);
}
