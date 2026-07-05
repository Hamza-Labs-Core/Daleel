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

    /// <summary>A store/brand catalogue with pricing. maxProducts ≤ 0 ⇒ uncapped (vendor ceiling).</summary>
    Task<IReadOnlyList<CatalogProduct>> ExtractCatalogAsync(
        string domain, int maxProducts = 0, int timeoutMs = 45_000, CancellationToken ct = default);

    /// <summary>Brand intelligence for a domain, or null when unconfigured/failed.</summary>
    Task<BrandProfile?> GetBrandAsync(string domain, CancellationToken ct = default);

    /// <summary>One page rendered to markdown/HTML, or null when unconfigured/failed-empty.</summary>
    Task<ScrapedPage?> ScrapePageAsync(
        string url, ScrapeFormat format = ScrapeFormat.Markdown, CancellationToken ct = default);

    /// <summary>
    /// Submits a catalogue crawl to the edge scrape-worker, metering the submit with the same
    /// per-call estimate an inline crawl would record (the vendor work happens on the edge, but the
    /// spend belongs to THIS job). Null when the edge is unconfigured or the submit failed.
    /// </summary>
    Task<WorkerHandle?> SubmitEdgeCatalogAsync(
        string domain, string? store, string? searchJobId, int maxProducts = 0, CancellationToken ct = default);
}

public sealed class ProviderApi : IProviderApi
{
    private readonly IAgentFactory _factory;
    private readonly ICloudflareWorkerClient? _edge;
    private readonly object _gate = new();
    private ContextDevProvider? _contextDev;
    private string? _contextDevKey;

    public ProviderApi(IAgentFactory factory, ICloudflareWorkerClient? edge = null)
    {
        _factory = factory;
        _edge = edge;
    }

    public bool HasScraper => _factory.Resolve("CONTEXT_DEV_API_KEY") is not null;

    public bool HasEdge => _edge is not null;

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

        // Metered as the crawl it triggers ("context.dev"-family provider name keeps the cost
        // estimator's extract rate), so edge and inline crawls hit the cap and the usage log
        // identically. Recorded ONLY on an accepted submit: a failed/rejected submit costs nothing
        // and falls back to the inline path, whose own metering then records the crawl — recording
        // here too would double-charge the same crawl. The drain records outcome/actuals as timeline
        // events; it does NOT record a second ApiCall — this one is the whole crawl's accounting.
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
        string provider, string endpoint, string? summary, Func<Task<T>> action, Func<T, long>? bytes = null) =>
        ApiCallTimer.TimeAsync(
            AmbientApiObserver.Observer,
            AmbientApiObserver.Estimator ?? new CostEstimator(),
            provider, endpoint, summary, action, bytes);
}
