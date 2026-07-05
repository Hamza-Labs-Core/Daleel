using System.Text.Json;
using Daleel.Search.Providers;
using Daleel.Web.Data;
using Daleel.Web.Profiles;
using Daleel.Web.Services;
using Daleel.Web.Storage;

namespace Daleel.Web.Identification;

/// <summary>
/// Discovers a brand's models across its regional sites (Jordan, the GCC, then global) so a vague
/// in-market listing has a catalogue to be matched against. For each <see cref="BrandRegions"/> candidate
/// domain it pulls the product catalogue via Context.dev, copies every image into R2, and upserts a
/// <see cref="BrandModel"/> per model — recording the region-specific name/SKU as a
/// <see cref="BrandModel.RegionalAliases"/> entry so the same product found under different regional names
/// converges onto one canonical row over time.
/// </summary>
public interface IBrandCatalogSearcher
{
    /// <summary>
    /// Returns the brand's known models, discovering them across regions first when the catalogue is empty
    /// or stale. Best-effort: never throws; returns whatever is already stored when discovery can't run.
    /// </summary>
    Task<IReadOnlyList<BrandModel>> DiscoverAsync(Brand brand, CancellationToken ct = default);
}

public sealed class BrandCatalogSearcher : IBrandCatalogSearcher
{
    /// <summary>Models harvested per regional site per pass — uncapped (0 ⇒ the vendor's own ceiling);
    /// the per-region crawl budget below bounds time, not count.</summary>
    private const int MaxModelsPerRegion = 0;

    /// <summary>How many regional candidate sites we'll probe before stopping (cost guard).</summary>
    private const int MaxRegionsProbed = 4;

    /// <summary>Per-region crawl budget, kept under the background enrichment timeout.</summary>
    private const int CrawlTimeoutMs = 20_000;

    private readonly IBrandModelRepository _models;
    private readonly IAgentFactory _factory;
    private readonly ProfileOptions _options;
    private readonly ILogger<BrandCatalogSearcher> _logger;

    public BrandCatalogSearcher(
        IBrandModelRepository models, IAgentFactory factory,
        ProfileOptions options, ILogger<BrandCatalogSearcher> logger)
    {
        _models = models;
        _factory = factory;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BrandModel>> DiscoverAsync(Brand brand, CancellationToken ct = default)
    {
        var now = _options.Now();
        var existing = await _models.ListByBrandAsync(brand.Id, ct).ConfigureAwait(false);

        // Fresh catalogue → reuse it (don't re-crawl a brand surfaced by many searches every time).
        if (existing.Count > 0 && existing.All(m => !m.IsStale(now, _options.Ttl)))
        {
            return existing;
        }

        var root = BrandRegions.RootDomain(brand.Website);
        var key = _factory.Resolve("CONTEXT_DEV_API_KEY");
        if (root is null || string.IsNullOrWhiteSpace(key))
        {
            return existing; // can't synthesize regional sites or no scraper key — keep what we have
        }

        var candidates = BrandRegions.CandidatesFor(root);
        var discovered = new Dictionary<int, BrandModel>();
        var regionsProbed = 0;

        foreach (var (region, domain) in candidates)
        {
            if (regionsProbed >= MaxRegionsProbed)
            {
                break;
            }

            ct.ThrowIfCancellationRequested();

            IReadOnlyList<CatalogProduct> catalogue;
            try
            {
                var ctx = new ContextDevProvider(key);
                // Metered through the ambient per-job observer — this crawl runs on its own provider
                // instance, invisible to the AgentFactory's wiring (see AmbientApiObserver).
                catalogue = await Daleel.Core.Observability.ApiCallTimer.TimeAsync(
                    Daleel.Core.Observability.AmbientApiObserver.Observer,
                    Daleel.Core.Observability.AmbientApiObserver.Estimator ?? new Daleel.Core.Observability.CostEstimator(),
                    "Context.dev", "catalog/extract", domain,
                    () => ctx.ExtractProductsAsync(domain, MaxModelsPerRegion, CrawlTimeoutMs, ct))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Regional catalogue probe failed for {Brand} at {Domain}", brand.Name, domain);
                continue;
            }

            regionsProbed++;
            if (catalogue.Count == 0)
            {
                continue;
            }

            foreach (var product in catalogue)
            {
                if (string.IsNullOrWhiteSpace(product.Name))
                {
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                var saved = await UpsertModelAsync(brand, region, product, now, ct).ConfigureAwait(false);
                if (saved is not null)
                {
                    discovered[saved.Id] = saved;
                }
            }
        }

        if (discovered.Count == 0)
        {
            return existing;
        }

        _logger.LogInformation("Discovered {Count} model(s) for {Brand} across {Regions} region(s)",
            discovered.Count, brand.Name, regionsProbed);

        // Return the union of freshly-discovered rows and any pre-existing ones not touched this pass.
        var merged = discovered.Values.ToDictionary(m => m.Id);
        foreach (var m in existing)
        {
            merged.TryAdd(m.Id, m);
        }

        return merged.Values.ToList();
    }

    private async Task<BrandModel?> UpsertModelAsync(
        Brand brand, BrandRegion region, CatalogProduct product, DateTimeOffset now, CancellationToken ct)
    {
        // Product images are the original source URL — rendered directly by the UI (external https images
        // are CSP-allowed and hot-linked). We no longer copy product shots into R2: the "hosted" URL relied
        // on the images bucket's public host matching the write target, and any mismatch silently 404'd every
        // image. Keeping the source URL mirrors the live search path and removes that failure mode entirely.
        var imageUrl = string.IsNullOrWhiteSpace(product.ImageUrl) ? null : product.ImageUrl!.Trim();

        // The region-specific SKU/name is recorded as an alias so a store quoting that local model number
        // still resolves to this row. The canonical ModelName stays the product's display name.
        var aliases = new List<string>();
        if (!string.IsNullOrWhiteSpace(product.Sku))
        {
            aliases.Add(product.Sku!.Trim());
        }

        var imageR2Urls = imageUrl is { Length: > 0 } && imageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new List<string> { imageUrl }
            : new List<string>();

        try
        {
            return await _models.UpsertAsync(new BrandModel
            {
                BrandId = brand.Id,
                ModelName = product.Name,
                ModelKey = BrandModel.Normalize(product.Name),
                Category = product.Category,
                SpecsJson = BuildSpecs(product),
                ImageUrl = imageUrl,
                ImageR2Urls = imageR2Urls,
                RegionalAliases = aliases,
                LocalPrice = product.Price,
                Currency = product.Currency,
                IsAvailable = true,
                IsDiscontinued = false,
                SourceUrl = product.Url,
                DiscoveredAt = now,
                LastRefreshed = now
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Persisting discovered model {Model} for {Brand} ({Region}) failed",
                product.Name, brand.Name, region.Name);
            return null;
        }
    }

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
}
