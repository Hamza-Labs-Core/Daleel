using System.Text.Json;
using Daleel.Search.Providers;
using Daleel.Web.Data;
using Daleel.Web.Services;
using Daleel.Web.Storage;

namespace Daleel.Web.Profiles;

/// <summary>
/// Builds out a brand's model database by scraping the brand's own (local/regional) website. For a saved
/// <see cref="Brand"/> with a known website it pulls the catalogue — models, specs, prices, images — via
/// Context.dev's structured product endpoint, copies each image into R2, and upserts a <see cref="BrandModel"/>
/// per model. This is the per-brand "sub-workflow" the search pipeline triggers during background enrichment.
/// </summary>
public interface IBrandCatalogService
{
    /// <summary>
    /// Resolves the saved brand by name and harvests its site into <see cref="BrandModel"/> rows. Returns the
    /// number of models upserted (0 when the brand is unknown, has no website, the scraper isn't configured,
    /// or its catalogue is already fresh). Best-effort: never throws for an individual brand.
    /// </summary>
    Task<int> HarvestAsync(string brandName, CancellationToken ct = default);
}

public sealed class BrandCatalogService : IBrandCatalogService
{
    /// <summary>Models harvested per brand per pass — uncapped (0 ⇒ the vendor's own ceiling); the
    /// per-brand crawl budget below bounds time, not count.</summary>
    private const int MaxModels = 0;

    /// <summary>Per-brand crawl budget, kept under the background enrichment timeout.</summary>
    private const int CrawlTimeoutMs = 30_000;

    private readonly IBrandRepository _brands;
    private readonly IBrandModelRepository _models;
    private readonly IAgentFactory _factory;
    private readonly ProfileOptions _options;
    private readonly ILogger<BrandCatalogService> _logger;
    private readonly Services.IProviderApi _providers;
    private readonly Data.ISystemConfigService? _config;

    public BrandCatalogService(
        IBrandRepository brands, IBrandModelRepository models,
        IAgentFactory factory, ProfileOptions options, ILogger<BrandCatalogService> logger,
        Services.IProviderApi? providers = null, Data.ISystemConfigService? config = null)
    {
        _brands = brands;
        _models = models;
        _factory = factory;
        _options = options;
        _logger = logger;
        // Optional so existing test wiring keeps working; production DI always supplies both.
        _providers = providers ?? new Services.ProviderApi(factory);
        _config = config;
    }

    public async Task<int> HarvestAsync(string brandName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brandName))
        {
            return 0;
        }

        var brand = await _brands.GetByNameAsync(brandName, ct).ConfigureAwait(false);
        var domain = DomainOf(brand?.Website);
        if (brand is null || domain is null)
        {
            return 0;
        }

        if (!_providers.HasScraper && !_providers.HasEdge)
        {
            return 0;
        }

        var now = _options.Now();

        // Skip if we already have a fresh catalogue for this brand — re-harvest only after the TTL so a
        // brand surfaced by many searches isn't re-crawled every time.
        var existing = await _models.ListByBrandAsync(brand.Id, ct).ConfigureAwait(false);
        if (existing.Count > 0 && existing.All(m => !m.IsStale(now, _options.Ttl)))
        {
            return 0;
        }

        // EDGE PATH (scrape-worker /scrape/brand): submit-and-forget — the crawl runs on the edge
        // with no local timeout racing it, and the drain's brand handler persists the models when
        // the result lands (even after this enrichment pass is long gone). Same gate as the store
        // crawl: full drain path + the admin flag; anything less falls through to inline.
        if (_providers.HasEdge && _providers.EdgeDrainReady &&
            _config is not null &&
            await _config.GetBoolAsync(
                Daleel.Web.Cloudflare.CloudflareWorkerOptions.EnabledFlag, false, ct).ConfigureAwait(false))
        {
            var handle = await _providers.SubmitEdgeBrandAsync(domain, brand.Name, searchJobId: null, ct)
                .ConfigureAwait(false);
            if (handle is not null)
            {
                _logger.LogInformation(
                    "Brand catalogue harvest for {Brand} ({Domain}) submitted to scrape-worker as job {JobId}",
                    brand.Name, domain, handle.JobId);
                return 0; // the drain persists the models whenever the edge job lands
            }
        }

        if (!_providers.HasScraper)
        {
            return 0; // edge unavailable and no inline scraper either
        }

        IReadOnlyList<CatalogProduct> catalogue;
        try
        {
            // Through the gateway — metered by construction (ambient per-job observer).
            catalogue = await _providers.ExtractCatalogAsync(domain, MaxModels, CrawlTimeoutMs, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Brand catalogue harvest failed for {Brand} ({Domain})", brand.Name, domain);
            return 0;
        }

        var harvested = 0;
        var dropped = 0;
        foreach (var product in catalogue)
        {
            if (string.IsNullOrWhiteSpace(product.Name))
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();

            // Product images are the original source URL — pulled from the catalogue/search results and
            // rendered directly (the UI hot-links external https images; CSP allows them). We deliberately do
            // NOT copy product shots into R2: that depended on the images bucket's public host being bound to
            // the exact bucket we write to, and a mismatch silently 404'd every "hosted" image. Keeping the
            // source URL matches the live search path and removes that whole failure mode.
            var imageUrl = string.IsNullOrWhiteSpace(product.ImageUrl) ? null : product.ImageUrl!.Trim();

            try
            {
                await _models.UpsertAsync(new BrandModel
                {
                    BrandId = brand.Id,
                    ModelName = product.Name,
                    ModelKey = BrandModel.Normalize(product.Name),
                    Category = product.Category,
                    SpecsJson = BuildSpecs(product),
                    ImageUrl = imageUrl,
                    LocalPrice = product.Price,
                    Currency = product.Currency,
                    // A product listed in the live catalogue is taken to be available; the brand site drops
                    // discontinued models rather than flagging them, so "listed" is the best signal we have.
                    IsAvailable = true,
                    SourceUrl = product.Url,
                    LastRefreshed = now
                }, ct).ConfigureAwait(false);
                harvested++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                dropped++;
                _logger.LogDebug(ex, "Persisting brand model {Model} for {Brand} failed", product.Name, brand.Name);
            }
        }

        // Surface dropped models at Warning: a single drop is benign, but a brand where many models fail to
        // persist signals a systematic problem (a constraint error, a poisoned tracker) that would otherwise
        // be invisible — the harvest just looks like "this brand had few models".
        if (dropped > 0)
        {
            _logger.LogWarning("Brand catalogue harvest for {Brand} dropped {Dropped} of {Total} model(s) on persist",
                brand.Name, dropped, dropped + harvested);
        }

        return harvested;
    }

    /// <summary>Serializes a catalogue product's free-form detail (description/SKU) as a small JSON object.</summary>
    private static string? BuildSpecs(CatalogProduct product)
    {
        var specs = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(product.Description))
        {
            specs["description"] = product.Description!;
        }

        if (!string.IsNullOrWhiteSpace(product.Sku))
        {
            specs["sku"] = product.Sku!;
        }

        return specs.Count == 0 ? null : JsonSerializer.Serialize(specs);
    }

    private static string? DomainOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var s = url.Trim();
        if (!s.Contains("://", StringComparison.Ordinal))
        {
            s = "https://" + s;
        }

        if (!Uri.TryCreate(s, UriKind.Absolute, out var u))
        {
            return null;
        }

        return u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
    }
}
