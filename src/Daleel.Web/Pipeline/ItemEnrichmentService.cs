using Daleel.Agent;
using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Core.Pricing;
using Daleel.Search.Abstractions;
using Daleel.Search.Providers;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Profiles;
using Daleel.Web.Services;
using Daleel.Web.Storage;
using Microsoft.Extensions.Logging;

namespace Daleel.Web.Pipeline;

/// <summary>Outcome of an enrichment pass: the updated products (null when nothing changed) + the events to record.</summary>
public sealed record ItemEnrichmentResult(ProductSearchResult? Products, IReadOnlyList<PipelineEvent> Events);

/// <summary>
/// The per-item deep-dive, run AFTER the base results are shown so it never blocks first paint. For
/// each product it compares store prices and fills in specs — DB-first (a fresh saved
/// <see cref="ProductProfile"/> is reused with no network), and on a miss it scrapes the brand's
/// official page for the model (that's where Context.dev earns its keep: authoritative, region-correct
/// specs) and saves the result for next time.
/// </summary>
public interface IItemEnrichmentService
{
    Task<ItemEnrichmentResult> EnrichAsync(
        AgentService agent, ProductSearchResult products, Action<string> progress,
        string? searchId, CancellationToken ct);
}

public sealed class ItemEnrichmentService : IItemEnrichmentService
{
    /// <summary>How many items get a price-comparison + reuse pass (DB reads only).</summary>
    private const int MaxItems = 20;

    /// <summary>How many NEW (uncached) thin items get an actual network scrape per run.</summary>
    private const int MaxNewScrapes = 8;

    /// <summary>An item is "thin" (worth scraping) when it has fewer than this many specs.</summary>
    private const int ThinSpecThreshold = 3;

    /// <summary>Cap on a saved detail blob (entity column is 8000).</summary>
    private const int MaxDetailChars = 4000;

    /// <summary>How many of the found stores' catalogues we harvest for live prices (each is a slow crawl).</summary>
    private const int MaxCatalogSites = 2;

    /// <summary>Per-catalogue crawl budget — kept under the background enrichment timeout.</summary>
    private const int CatalogTimeoutMs = 30_000;

    /// <summary>
    /// Absolute floor on shared significant tokens (brand/model/SKU) before a price is attributed to an
    /// item — paired with <see cref="MinMatchRatio"/>. Kept at 2 only to kill the degenerate single-token
    /// (100%-of-a-1-token-name) match; the ratio below does the real tightening.
    /// </summary>
    private const int MinMatchTokens = 2;

    /// <summary>
    /// Fraction of the SHORTER name's tokens that must be shared to trust a catalogue/scrape→item price
    /// match. The old flat "any 2 shared tokens" ignored name length, so a long title sharing only an
    /// incidental brand word passed; requiring 80% of the shorter side makes attribution length-aware.
    /// </summary>
    private const double MinMatchRatio = 0.8;

    private readonly IProductProfileRepository _repo;
    private readonly ProfileOptions _options;
    private readonly IAgentFactory _factory;
    private readonly IBrandRepository _brands;
    private readonly IBrandModelRepository _brandModels;
    private readonly Identification.IProductIdentifier _identifier;
    private readonly IScrapedPriceRepository _scrapedPrices;
    private readonly IBrandCatalogService _brandCatalog;
    private readonly ILogger<ItemEnrichmentService> _logger;

    /// <summary>How many of the result's brands get their site catalogue harvested per run (slow crawls).</summary>
    private const int MaxBrandCatalogs = 2;

    /// <summary>
    /// Cap on per-run image-search lookups for models the pipeline left imageless (~one grid page).
    /// Each is a paid SerpAPI call, so the backfill fills what the user sees first, not the long tail.
    /// </summary>
    private const int MaxImageLookups = 12;

    /// <summary>
    /// Cap on per-run VISION identifications (each can spend catalog crawls + up to 8 paid vision
    /// comparisons). Runs only for items text-matching couldn't identify but that carry a photo —
    /// the photo IS the identity signal for vaguely-titled marketplace listings.
    /// </summary>
    private const int MaxVisionIdentifications = 4;

    /// <summary>
    /// Hard wall-clock budget for the whole vision-identification phase, so a hung discovery crawl
    /// or slow vision endpoint costs THIS phase only — never the rest of the enrichment window.
    /// The phase's paid calls are metered through <see cref="Daleel.Core.Observability.AmbientApiObserver"/>,
    /// so they also count toward the per-job cost cap and appear in the usage dashboard.
    /// </summary>
    private const int VisionPhaseBudgetSeconds = 150;

    public ItemEnrichmentService(
        IProductProfileRepository repo, ProfileOptions options, IAgentFactory factory,
        IScrapedPriceRepository scrapedPrices, IBrandCatalogService brandCatalog,
        IBrandRepository brands, IBrandModelRepository brandModels,
        Identification.IProductIdentifier identifier,
        ILogger<ItemEnrichmentService> logger)
    {
        _repo = repo;
        _options = options;
        _factory = factory;
        _scrapedPrices = scrapedPrices;
        _brandCatalog = brandCatalog;
        _brands = brands;
        _brandModels = brandModels;
        _identifier = identifier;
        _logger = logger;
    }

    public async Task<ItemEnrichmentResult> EnrichAsync(
        AgentService agent, ProductSearchResult products, Action<string> progress,
        string? searchId, CancellationToken ct)
    {
        var events = new List<PipelineEvent>();
        void Record(string category, string type, string provider,
            IReadOnlyDictionary<string, object?>? meta = null) =>
            events.Add(PipelineEventFactory.Custom(category, type, provider, searchId, metadata: meta));

        if (products.Models.Count == 0)
        {
            return new ItemEnrichmentResult(null, events);
        }

        var now = _options.Now();
        var ttl = _options.Ttl;
        // ALL models flow through the cheap phases (DB read-through, in-memory catalogue match);
        // only the PAID phases (page scrapes, image search) are capped to the first MaxItems.
        var models = products.Models.ToList();
        var headCount = Math.Min(MaxItems, models.Count);
        var enriched = new Dictionary<int, string>();

        // Phase 0 — brand-database read-through: images, brand-site prices, and specs harvested by
        // PREVIOUS searches fill this result before any network call is spent. This is what makes
        // the store/brand pipeline compound: every harvest pays off on every later search.
        var (filled0, dbImages, dbPrices, dbSpecs) = await FillFromBrandDatabaseAsync(models, products.Geo, ct);
        models = filled0;
        if (dbImages + dbPrices + dbSpecs > 0)
        {
            progress($"Matched {dbImages + dbPrices + dbSpecs} detail(s) from the product database.");
            Record(EventCategory.Profile, "item.dbfill", "brand-db",
                new Dictionary<string, object?> { ["images"] = dbImages, ["prices"] = dbPrices, ["specs"] = dbSpecs });
        }

        // Phase 0.5 — VISION identification for items text-matching couldn't place: a vaguely-titled
        // marketplace listing ("Automatic Washing Machine - Open Market") with a PHOTO is identified
        // by comparing that photo against the brand's catalogue images (SmartProductIdentifier's
        // text → catalogue-discovery → vision chain, memoized in VisionMatchCache). An identified
        // item then fills image/specs/price from its canonical BrandModel row and gains the canonical
        // model name — the strategy that works precisely where token matching fails. Capped: each
        // identification can spend catalogue crawls plus paid vision comparisons.
        var visionFills = await IdentifyViaVisionAsync(models, products.Geo, progress, Record, ct);

        // Phase 1 — price comparison + DB-first reuse (sequential: scoped DbContext isn't concurrency-safe).
        var toScrape = new List<(ProductModel m, int idx, string url, string key)>();
        for (var idx = 0; idx < headCount; idx++)
        {
            var m = models[idx];
            var stores = m.Offers.Select(o => o.Source)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            progress($"Getting details on {m.Name} — comparing {stores} store price(s)…");
            Record(EventCategory.Extract, "item.compare", "pipeline",
                new Dictionary<string, object?> { ["item"] = m.Name, ["stores"] = stores, ["offers"] = m.Offers.Count });

            var key = ProductProfile.KeyFor(m.Brand, m.Model, m.Name);
            if (key.Length == 0)
            {
                continue;
            }

            var saved = await SafeGet(key, ct);
            if (saved is { } s && !s.IsStale(now, ttl) && !string.IsNullOrWhiteSpace(s.Details))
            {
                enriched[idx] = s.Details!;
                Record(EventCategory.Profile, "item.reuse", "profile",
                    new Dictionary<string, object?> { ["item"] = m.Name, ["cached"] = true });
                continue;
            }

            var url = OfficialOrCheapestUrl(m);
            if (url is not null && m.Specs.Count < ThinSpecThreshold && toScrape.Count < MaxNewScrapes)
            {
                toScrape.Add((m, idx, url, key));
            }
        }

        // Phase 2 — scrape the misses concurrently (network only, no DB).
        var scraped = toScrape.Count == 0
            ? Array.Empty<(int idx, string url, string key, ProductModel m, string? content)>()
            : await Task.WhenAll(toScrape.Select(async t =>
            {
                progress($"Fetching official specs for {t.m.Name}…");
                var page = await agent.ReadPageAsync(t.url, ct);
                var content = page is null
                    ? null
                    : page.Content.Length <= MaxDetailChars ? page.Content : page.Content[..MaxDetailChars];
                return (t.idx, t.url, t.key, t.m, content);
            }));

        // Phase 3 — persist each fresh deep-dive (sequential writes) so it's reused next time.
        var fresh = 0;
        foreach (var r in scraped)
        {
            if (string.IsNullOrWhiteSpace(r.content))
            {
                continue;
            }

            enriched[r.idx] = r.content!;
            fresh++;
            Record(EventCategory.Extract, "item.deepdive", "context.dev",
                new Dictionary<string, object?> { ["item"] = r.m.Name, ["url"] = r.url });
            await SafeUpsert(new ProductProfile
            {
                Name = r.m.Name, Brand = r.m.Brand, Model = r.m.Model, NameKey = r.key,
                Details = r.content, SourceUrl = r.url, LastRefreshed = now
            }, ct);
        }

        // Merge any fresh/cached spec details into the working models.
        var withSpecs = models
            .Select((m, idx) =>
            {
                if (!enriched.TryGetValue(idx, out var detail))
                {
                    return m;
                }
                var specs = new Dictionary<string, string>(m.Specs) { ["details"] = detail };
                return m with { Specs = specs };
            })
            .ToList();

        // Phase 4 — STORE catalogues (Context.dev /v1/brand/ai/products, browser-render fallback):
        // every item gets matched, and a match contributes its price (when the item lacks one),
        // its product image, and its structured specs — the store data is the primary source, not
        // just a price gap-filler.
        var (pricedModels, priced, storeImages, storeSpecs) =
            await AttachCatalogDataAsync(agent, withSpecs, products, progress, Record, ct);

        // Phase 5 — harvest each surfaced brand's own site into the BrandModel database, then
        // IMMEDIATELY re-run the database read-through so this run's harvest benefits THIS result
        // (images/specs/prices from the brand's own catalogue), not only future searches.
        await HarvestBrandCatalogsAsync(products, progress, Record, ct);
        var (harvestFilled, brandImages, brandPrices, brandSpecs) =
            await FillFromBrandDatabaseAsync(pricedModels, products.Geo, ct);
        pricedModels = harvestFilled;
        if (brandImages + brandPrices + brandSpecs > 0)
        {
            progress($"Filled {brandImages + brandPrices + brandSpecs} detail(s) from brand catalogues.");
        }

        // Phase 6 — LAST-RESORT image search for whatever the store catalogues, brand sites, and
        // product database all failed to cover (common in markets where Google Shopping doesn't
        // operate). Capped paid lookups; a failed lookup just leaves the placeholder.
        var imagesFound = 0;
        var imageAttempts = 0;
        // The cap bounds paid ATTEMPTS, not successes — on a large grid of imageless obscure items,
        // counting only hits would keep paying for lookup after failed lookup with no ceiling.
        for (var i = 0; i < pricedModels.Count && imageAttempts < MaxImageLookups; i++)
        {
            var m = pricedModels[i];
            if (!string.IsNullOrWhiteSpace(m.ImageUrl))
            {
                continue;
            }

            imageAttempts++;

            var imageQuery = string.Join(' ', new[]
            {
                m.Brand,
                string.IsNullOrWhiteSpace(m.Model) ? m.Name : m.Model
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

            if (await agent.FindProductImageAsync(imageQuery, ct) is not { } img)
            {
                continue;
            }

            pricedModels[i] = m with { ImageUrl = img };
            imagesFound++;
            Record(EventCategory.Extract, "item.image", "serpapi",
                new Dictionary<string, object?> { ["item"] = m.Name });
        }

        if (imagesFound > 0)
        {
            progress($"Found product images for {imagesFound} item(s).");
        }

        var touched = enriched.Count + priced + imagesFound + visionFills
                      + dbImages + dbPrices + dbSpecs
                      + storeImages + storeSpecs
                      + brandImages + brandPrices + brandSpecs;
        if (touched == 0)
        {
            return new ItemEnrichmentResult(null, events);
        }

        progress(
            $"Deep-dived {enriched.Count} item(s) — {fresh} new, {enriched.Count - fresh} reused" +
            (priced > 0 ? $"; added live prices to {priced}." : "."));
        return new ItemEnrichmentResult(products with { Models = pricedModels }, events);
    }

    /// <summary>
    /// For items that still have no price, harvest the top found stores' product catalogues and attach a
    /// matching priced offer — and, for EVERY strong match, the catalogue product's image and its
    /// structured fields (description/category/sku) as specs, whether or not the item already had a
    /// price. Two sources: Context.dev's structured <c>/v1/brand/ai/products</c> first, then a
    /// Cloudflare-Browser-rendered page parsed by <see cref="PriceParser"/> for the stores Context.dev
    /// can't read (prices only — the browser pass carries no images). Matched prices are also written
    /// to the <see cref="ScrapedPrice"/> history; images stay as original source URLs (the UI renders
    /// external image URLs directly). Best-effort: any failure leaves the item as-is.
    /// </summary>
    private async Task<(List<ProductModel> Models, int Priced, int Images, int Specs)> AttachCatalogDataAsync(
        AgentService agent, List<ProductModel> models, ProductSearchResult products, Action<string> progress,
        Action<string, string, string, IReadOnlyDictionary<string, object?>?> record, CancellationToken ct)
    {
        // The store catalogues have something to offer whenever ANY item lacks a price, an image, or
        // meaningful specs — not only when a price is missing (the old gate starved images/specs).
        if (!models.Any(m => !HasPrice(m) || string.IsNullOrWhiteSpace(m.ImageUrl) || m.Specs.Count < ThinSpecThreshold))
        {
            return (models, 0, 0, 0);
        }

        var domains = products.Stores
            .Select(s => DomainOf(s.Url))
            .Where(d => d is not null).Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCatalogSites)
            .ToList();
        if (domains.Count == 0)
        {
            return (models, 0, 0, 0);
        }

        // Hard cap on the whole catalogue phase so a slow crawl (or a retry on the API's 408) can never
        // blow the background enrichment budget — if it overruns, we just ship the result without it.
        using var catalogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        catalogCts.CancelAfter(TimeSpan.FromMilliseconds(CatalogTimeoutMs + 8_000));

        // Primary: Context.dev's purpose-built catalogue extraction. Secondary (for the domains it can't
        // read — JS-heavy/anti-bot stores): render the page through the scrape router, which falls through
        // to the Cloudflare Browser renderer, and parse prices out of the markdown ourselves.
        var key = _factory.Resolve("CONTEXT_DEV_API_KEY");
        var pool = new List<CatalogProduct>();
        var browserUnpriced = domains;

        if (!string.IsNullOrWhiteSpace(key))
        {
            var ctx = new ContextDevProvider(key);
            progress($"Reading {domains.Count} store catalogue(s) for live prices…");

            var catalogues = await Task.WhenAll(domains.Select(async d =>
            {
                var found = await SafeCatalog(ctx, d, _logger, catalogCts.Token);
                record(EventCategory.Extract, "catalog.products", "context.dev",
                    new Dictionary<string, object?> { ["domain"] = d, ["products"] = found.Count });
                return (domain: d, found);
            }));

            // Keep EVERY catalogue product — an unpriced entry still carries the image and specs the
            // grid needs. Only the price-attach step below cares whether the entry has a price.
            pool = catalogues.SelectMany(c => c.found).ToList();
            // Only the domains Context.dev returned nothing priced for fall through to the browser pass.
            browserUnpriced = catalogues.Where(c => c.found.All(p => p.Price is null))
                .Select(c => c.domain).ToList();
        }

        var browserPrices = await HarvestViaBrowserAsync(agent, browserUnpriced, products.Query, record, catalogCts.Token);

        if (pool.Count == 0 && browserPrices.Count == 0)
        {
            return (models, 0, 0, 0);
        }

        var now = _options.Now();
        var observations = new List<ScrapedPrice>();
        var priced = 0;
        var images = 0;
        var specsFilled = 0;
        var updated = new List<ProductModel>(models.Count);
        foreach (var m in models)
        {
            // EVERY item gets catalogue-matched: a priced item can still gain its image and specs.
            var match = BestCatalogMatch(m, pool);
            var item = m;

            if (match is { } c)
            {
                if (!HasPrice(item) && c.Price is not null)
                {
                    var offer = new PriceOffer
                    {
                        Source = DomainOf(c.Url) ?? "Store", SourceType = ResultType.StorePage,
                        Price = c.Price, Currency = c.Currency, Url = c.Url, IsLocal = true
                    };
                    item = item with { Offers = item.Offers.Append(offer).ToList() };
                    priced++;
                    observations.Add(new ScrapedPrice
                    {
                        ProductName = m.Name,
                        ProductKey = ProductProfile.KeyFor(m.Brand, m.Model, m.Name),
                        StoreName = offer.Source,
                        Price = offer.Price,
                        Currency = offer.Currency,
                        SourceUrl = offer.Url,
                        Provider = "context.dev",
                        ScrapedAt = now
                    });
                }

                // The catalogue entry usually carries a product image — fill it in when the model has
                // none, keeping the original source URL (the UI renders external image URLs directly).
                if (string.IsNullOrWhiteSpace(item.ImageUrl) && c.ImageUrl is { Length: > 0 } img)
                {
                    item = item with { ImageUrl = img };
                    images++;
                }

                // Structured catalogue fields become specs (existing keys always win; description capped).
                var withCatalogSpecs = MergeCatalogSpecs(item, c);
                if (!ReferenceEquals(withCatalogSpecs, item))
                {
                    item = withCatalogSpecs;
                    specsFilled++;
                }
            }
            // Independent of the catalogue match: the chosen entry may carry NO parsed price, and an
            // unpriced match must not block a browser-scraped line price from filling the gap.
            if (!HasPrice(item) && BestBrowserMatch(m, browserPrices) is { } bp)
            {
                // Browser-rendered fallback carries prices only (no image/spec data in a price line) —
                // and a price regex-parsed out of rendered page text is a POTENTIAL price, not a quote:
                // mark it indicative so the UI says "≈ verify at the store" and leads there.
                var offer = new PriceOffer
                {
                    Source = bp.Store, SourceType = ResultType.StorePage,
                    Price = bp.Price, Currency = bp.Currency, Url = bp.Url, IsLocal = true,
                    IsIndicative = true
                };
                item = item with { Offers = item.Offers.Append(offer).ToList() };
                priced++;
                observations.Add(new ScrapedPrice
                {
                    ProductName = m.Name,
                    ProductKey = ProductProfile.KeyFor(m.Brand, m.Model, m.Name),
                    StoreName = offer.Source,
                    Price = offer.Price,
                    Currency = offer.Currency,
                    SourceUrl = offer.Url,
                    Provider = "cloudflare-browser",
                    ScrapedAt = now
                });
            }

            updated.Add(item);
        }

        // Persist the observations as a timestamped batch — the price history the comparison view reads.
        await PersistObservations(observations, record, ct);

        if (priced + images + specsFilled > 0)
        {
            progress($"Store catalogues matched {priced} price(s), {images} image(s), {specsFilled} spec set(s).");
        }
        return (updated, priced, images, specsFilled);
    }

    /// <summary>Merges a catalogue product's structured fields into the model's specs (existing keys win).</summary>
    private static ProductModel MergeCatalogSpecs(ProductModel m, CatalogProduct c)
    {
        Dictionary<string, string>? merged = null;
        void Put(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || m.Specs.ContainsKey(key))
            {
                return;
            }

            merged ??= new Dictionary<string, string>(m.Specs, StringComparer.OrdinalIgnoreCase);
            merged[key] = value!;
        }

        Put("category", c.Category);
        Put("sku", c.Sku);
        Put("description", c.Description is { Length: > 500 } d ? d[..500] + "…" : c.Description);
        return merged is null ? m : m with { Specs = merged };
    }

    /// <summary>
    /// DB read-through over the harvested BrandModel database — the images, brand-site prices, and
    /// specs every PREVIOUS search already paid to collect. Runs before any paid call (and again
    /// right after this run's own brand harvest) so the app's own product database — not an external
    /// image search — is the primary source. Pure local DB reads over ALL items; best-effort.
    /// </summary>
    private async Task<(List<ProductModel> Models, int Images, int Prices, int Specs)> FillFromBrandDatabaseAsync(
        List<ProductModel> models, string? geo, CancellationToken ct)
    {
        var images = 0;
        var prices = 0;
        var specsFilled = 0;
        var now = _options.Now();

        // BrandModel rows carry ONE LocalPrice/Currency (last harvest wins, no region column), so a
        // Jordan harvest's JOD price must never surface as an offer on a USA search. Attach a
        // brand-DB price only when its currency IS the search market's currency; images and specs
        // are market-agnostic and flow regardless.
        var marketCurrency = Daleel.Core.Geo.GeoProfiles.ResolveOrDefault(geo).Currency;

        // One lookup per distinct brand; the brand's rows are reused across all its items.
        var rowsByBrand = new Dictionary<string, IReadOnlyList<BrandModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in models.Select(m => m.Brand?.Trim())
                     .Where(b => !string.IsNullOrWhiteSpace(b))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (await _brands.GetByNameAsync(name!, ct) is not { } brand)
                {
                    continue;
                }

                var rows = await _brandModels.ListByBrandAsync(brand.Id, ct);
                if (rows.Count > 0)
                {
                    rowsByBrand[name!] = rows;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Brand-model lookup failed for {Brand}", name);
            }
        }

        if (rowsByBrand.Count == 0)
        {
            return (models, 0, 0, 0);
        }

        for (var i = 0; i < models.Count; i++)
        {
            var m = models[i];
            if (m.Brand is null || !rowsByBrand.TryGetValue(m.Brand.Trim(), out var rows))
            {
                continue;
            }

            if (BestBrandModelMatch(m, rows) is not { } match)
            {
                continue;
            }

            var (updated, filledImage, filledPrice, filledSpecs) = FillFromRow(m, match, marketCurrency, now);
            if (filledImage) images++;
            if (filledPrice) prices++;
            if (filledSpecs) specsFilled++;
            if (!ReferenceEquals(updated, m))
            {
                models[i] = updated;
            }
        }

        return (models, images, prices, specsFilled);
    }

    /// <summary>
    /// Vision-identifies items the text matchers couldn't place (they have a brand and a PHOTO but a
    /// vague/listing-style title) and fills them from their identified canonical BrandModel row.
    /// The identifier's chain is cheapest-first (text → catalogue discovery → vision compare, with
    /// the vision verdicts memoized), so repeat searches get the same identification for free.
    /// Best-effort per item; capped per run.
    /// </summary>
    private async Task<int> IdentifyViaVisionAsync(
        List<ProductModel> models, string? geo, Action<string> progress,
        Action<string, string, string, IReadOnlyDictionary<string, object?>?> record, CancellationToken ct)
    {
        var marketCurrency = Daleel.Core.Geo.GeoProfiles.ResolveOrDefault(geo).Currency;
        var now = _options.Now();
        var attempts = 0;
        var fills = 0;

        // Hard PHASE budget (same pattern as the store-catalogue phase's catalogCts): the identifier's
        // inner limits are partly advisory — the discovery crawl's "20s" rides in the request body only
        // (a hung Context.dev call can burn ~100s × retries client-side) and each vision call can take
        // up to 60s — so without this cap one slow item could eat the entire enrichment window and the
        // outer timeout would then discard EVERY phase's work. When this budget expires we keep what
        // was identified and simply move on to the remaining phases.
        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        phaseCts.CancelAfter(TimeSpan.FromSeconds(VisionPhaseBudgetSeconds));

        // One identification attempt per BRAND per run: a failed catalogue discovery for a brand would
        // fail identically for its other items seconds later — repeating it just re-pays the crawls.
        var attemptedBrands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < models.Count && attempts < MaxVisionIdentifications; i++)
        {
            var m = models[i];
            // Only items where vision can actually add signal: a photo to compare, a brand to scope
            // the catalogue, and something still missing that an identification would fill.
            var needsHelp = !HasPrice(m) || m.Specs.Count < ThinSpecThreshold;
            if (!needsHelp || string.IsNullOrWhiteSpace(m.Brand) || string.IsNullOrWhiteSpace(m.ImageUrl))
            {
                continue;
            }

            // A gstatic thumbnail means the photo came from OUR OWN image-search backfill (a previous
            // run's last-resort phase), not from the listing — identifying against it would let a wrong
            // backfilled photo pick the product's identity (a feedback loop). Only listing-origin
            // photos carry identity signal.
            if (m.ImageUrl!.Contains("gstatic.com", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!attemptedBrands.Add(m.Brand!.Trim()))
            {
                continue;
            }

            attempts++;
            try
            {
                var id = await _identifier.IdentifyAsync(m, phaseCts.Token);
                if (!id.Matched || id.BrandModelId is not { } rowId ||
                    await _brandModels.GetByIdAsync(rowId, ct) is not { } row)
                {
                    continue;
                }

                var (filled, img, price, specs) = FillFromRow(m, row, marketCurrency, now);

                // The canonical model name unlocks correct dedup/compare downstream; only fill a blank.
                if (string.IsNullOrWhiteSpace(filled.Model) && !string.IsNullOrWhiteSpace(id.CanonicalModelName))
                {
                    filled = filled with { Model = id.CanonicalModelName };
                }

                if (!ReferenceEquals(filled, m))
                {
                    models[i] = filled;
                    if (img || price || specs) fills++;
                }

                record(EventCategory.Extract, "item.identify", "vision",
                    new Dictionary<string, object?>
                    {
                        ["item"] = m.Name, ["method"] = id.Method, ["confidence"] = id.Confidence,
                        ["canonical"] = id.CanonicalModelName
                    });
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The PHASE budget expired (the run itself is fine): keep what was identified so far
                // and hand the remaining time to the other enrichment phases.
                _logger.LogInformation(
                    "Vision identification phase hit its {Seconds}s budget after {Attempts} attempt(s); continuing",
                    VisionPhaseBudgetSeconds, attempts);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Vision identification failed for {Item}", m.Name);
            }
        }

        if (fills > 0)
        {
            progress($"Identified {fills} item(s) by product photo.");
        }

        return fills;
    }

    /// <summary>
    /// Applies one matched BrandModel row to an item: brand-catalogue image when the item has none,
    /// a fresh brand-site price (market-currency-gated) when it is priceless, and a specs merge.
    /// Shared by the text-match read-through and the vision-identification path.
    /// </summary>
    private (ProductModel Item, bool Image, bool Price, bool Specs) FillFromRow(
        ProductModel m, BrandModel match, string marketCurrency, DateTimeOffset now)
    {
        var updated = m;
        var filledImage = false;
        var filledPrice = false;

        // Brand-site catalogue image — locally accurate, and free: it's already in our DB.
        var dbImage = match.ImageR2Urls.FirstOrDefault() ?? match.ImageUrl;
        if (string.IsNullOrWhiteSpace(updated.ImageUrl) && !string.IsNullOrWhiteSpace(dbImage))
        {
            updated = updated with { ImageUrl = dbImage };
            filledImage = true;
        }

        // A fresh harvested brand-site price becomes an offer for a still-priceless item — but
        // ONLY in the market it was harvested for (currency must match the search market's).
        if (!HasPrice(updated) && match.LocalPrice is not null && !match.IsStale(now, _options.Ttl) &&
            string.Equals(match.Currency, marketCurrency, StringComparison.OrdinalIgnoreCase))
        {
            updated = updated with
            {
                Offers = updated.Offers.Append(new PriceOffer
                {
                    Source = DomainOf(match.SourceUrl) ?? m.Brand ?? "Brand site",
                    SourceType = ResultType.BrandPage,
                    Price = match.LocalPrice,
                    Currency = match.Currency,
                    Url = match.SourceUrl,
                    IsLocal = true
                }).ToList()
            };
            filledPrice = true;
        }

        var withSpecs = MergeBrandSpecs(updated, match);
        var filledSpecs = !ReferenceEquals(withSpecs, updated);
        return (withSpecs, filledImage, filledPrice, filledSpecs);
    }

    /// <summary>Merges the harvested row's category + SpecsJson keys into the model (existing keys win).</summary>
    private static ProductModel MergeBrandSpecs(ProductModel m, BrandModel row)
    {
        Dictionary<string, string>? merged = null;
        void Put(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || m.Specs.ContainsKey(key))
            {
                return;
            }

            merged ??= new Dictionary<string, string>(m.Specs, StringComparer.OrdinalIgnoreCase);
            merged[key] = value!;
        }

        Put("category", row.Category);
        if (!string.IsNullOrWhiteSpace(row.SpecsJson))
        {
            try
            {
                var specs = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, string>>(row.SpecsJson!);
                if (specs is not null)
                {
                    foreach (var (k, v) in specs)
                    {
                        Put(k, v);
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Malformed stored specs must never fault enrichment.
            }
        }

        return merged is null ? m : m with { Specs = merged };
    }

    /// <summary>
    /// Matches an item against the brand's known models. Exact normalized-key equality wins
    /// outright; otherwise candidates are token-matched with SHORT tokens kept (2+ chars — variant
    /// suffixes like "FE"/"5G"/"XL" must count, or "Galaxy S24" silently takes the S24 FE's price)
    /// and a candidate is only eligible when EVERY one of its model tokens appears in the item
    /// (a row naming a variant the item doesn't mention is a different product, not a match).
    /// Qualification is evaluated PER CANDIDATE — not argmax-then-test, which both let a long wrong
    /// candidate shadow a valid short one and let ties fall to arbitrary DB order.
    /// </summary>
    private static BrandModel? BestBrandModelMatch(ProductModel m, IReadOnlyList<BrandModel> rows)
    {
        // Exact identity first: the normalized model key is precisely what the harvesters store.
        var itemKey = BrandModel.Normalize(string.IsNullOrWhiteSpace(m.Model) ? m.Name : m.Model!);
        if (itemKey.Length > 0 && rows.FirstOrDefault(r => r.ModelKey == itemKey) is { } exact)
        {
            return exact;
        }

        var want = ShortTokens($"{m.Brand} {m.Model} {m.Name}");
        BrandModel? best = null;
        var bestShared = 0;

        foreach (var row in rows)
        {
            foreach (var candidate in Candidates(row))
            {
                var have = ShortTokens(candidate);
                if (have.Count == 0 || !have.IsSubsetOf(want))
                {
                    continue; // the row names something the item doesn't — a different variant/product
                }

                var shared = have.Count; // subset ⇒ shared == have.Count
                if (shared < MinMatchTokens)
                {
                    continue;
                }

                // Among eligible candidates prefer the MOST specific one (more shared tokens), so
                // "Galaxy S24 FE" beats bare "Galaxy S24" for an S24 FE item, never vice versa.
                if (shared > bestShared)
                {
                    best = row;
                    bestShared = shared;
                }
            }
        }

        return best;

        static IEnumerable<string> Candidates(BrandModel row)
        {
            yield return row.ModelName;
            if (!string.IsNullOrWhiteSpace(row.ModelKey))
            {
                yield return row.ModelKey;
            }

            foreach (var alias in row.RegionalAliases)
            {
                yield return alias;
            }
        }
    }

    /// <summary>
    /// Tokenizer for brand-model matching that KEEPS 2-character tokens: variant suffixes ("FE",
    /// "5G", "XL", "II") are exactly what distinguishes one model from another, so the general
    /// 3-char <see cref="Tokens"/> filter would blind the matcher where precision matters most.
    /// </summary>
    private static HashSet<string> ShortTokens(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return set;
        }

        foreach (var t in text.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (t.Length >= 2)
            {
                set.Add(t);
            }
        }

        return set;
    }

    /// <summary>
    /// Triggers the per-brand catalogue "sub-workflow" for the top few brands the search surfaced: each
    /// resolves the brand's site and upserts its models into the BrandModel database (see
    /// <see cref="IBrandCatalogService"/>). Capped and sequential so the slow crawls and the shared scoped
    /// DbContext stay within budget. Best-effort: a brand that fails is skipped.
    /// </summary>
    private async Task HarvestBrandCatalogsAsync(
        ProductSearchResult products, Action<string> progress,
        Action<string, string, string, IReadOnlyDictionary<string, object?>?> record, CancellationToken ct)
    {
        var brands = products.Brands
            .Select(b => b.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxBrandCatalogs)
            .ToList();
        if (brands.Count == 0)
        {
            return;
        }

        foreach (var name in brands)
        {
            ct.ThrowIfCancellationRequested();
            int harvested;
            try
            {
                harvested = await _brandCatalog.HarvestAsync(name, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { harvested = 0; }

            if (harvested > 0)
            {
                progress($"Catalogued {harvested} {name} model(s) from the brand site.");
                record(EventCategory.Extract, "brand.catalog", "context.dev",
                    new Dictionary<string, object?> { ["brand"] = name, ["models"] = harvested });
            }
        }
    }

    /// <summary>
    /// Browser-fallback price harvest: render each store's <em>on-site search page for the query</em> through
    /// the agent's scrape router (Context.dev → Cloudflare Browser), then pull candidate prices out of the
    /// markdown with <see cref="PriceParser"/>. Scraping the search results (not the homepage) keeps the
    /// harvested prices product-relevant — a homepage's featured/deal prices would be mis-attributed to our
    /// items. This is the path that earns the Cloudflare integration — JS-heavy/anti-bot stores the structured
    /// endpoint can't read still yield prices here. Best-effort: any failure just yields no prices.
    /// </summary>
    private static async Task<IReadOnlyList<BrowserPrice>> HarvestViaBrowserAsync(
        AgentService agent, IReadOnlyList<string> domains, string query,
        Action<string, string, string, IReadOnlyDictionary<string, object?>?> record, CancellationToken ct)
    {
        if (domains.Count == 0)
        {
            return Array.Empty<BrowserPrice>();
        }

        var harvested = await Task.WhenAll(domains.Select(async domain =>
        {
            var url = ProductSearchUrl(domain, query);
            ScrapedPage? page;
            try
            {
                page = await agent.ReadPageAsync(url, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { page = null; }

            var prices = page is null || string.IsNullOrWhiteSpace(page.Content)
                ? new List<BrowserPrice>()
                : PriceParser.Extract(page.Content)
                    .Select(p => new BrowserPrice(p.Price, p.Currency, p.Line, domain, page.Url))
                    .ToList();

            record(EventCategory.Extract, "catalog.browser", page?.Provider ?? "cloudflare-browser",
                new Dictionary<string, object?> { ["domain"] = domain, ["url"] = url, ["prices"] = prices.Count });
            return (IReadOnlyList<BrowserPrice>)prices;
        }));

        return harvested.SelectMany(h => h).ToList();
    }

    /// <summary>
    /// A store's on-site search URL for <paramref name="query"/>, so the browser fallback renders a
    /// product-relevant page (results for what the user searched) rather than the homepage — whose
    /// featured/deal prices would be mis-attributed to our items. Uses the widely-supported
    /// <c>/search?q=</c> convention; falls back to the bare domain only when there's no query.
    /// </summary>
    private static string ProductSearchUrl(string domain, string query)
    {
        var root = domain.Contains("://", StringComparison.Ordinal) ? domain : $"https://{domain}";
        if (string.IsNullOrWhiteSpace(query))
        {
            return root;
        }

        return $"{root.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}";
    }

    /// <summary>Best browser-scraped price for a model: the line sharing the most brand/model tokens.</summary>
    private static BrowserPrice? BestBrowserMatch(ProductModel m, IReadOnlyList<BrowserPrice> pool)
    {
        if (pool.Count == 0)
        {
            return null;
        }

        var want = Tokens($"{m.Brand} {m.Model} {m.Name}");
        if (want.Count == 0)
        {
            return null;
        }

        BrowserPrice? best = null;
        var bestScore = 0;
        var bestHave = 0;
        foreach (var p in pool)
        {
            var have = Tokens(p.Line);
            var score = have.Count(want.Contains);
            if (score > bestScore)
            {
                best = p;
                bestScore = score;
                bestHave = have.Count;
            }
        }

        return IsStrongMatch(bestScore, want.Count, bestHave) ? best : null;
    }

    private async Task PersistObservations(
        List<ScrapedPrice> observations,
        Action<string, string, string, IReadOnlyDictionary<string, object?>?> record, CancellationToken ct)
    {
        if (observations.Count == 0)
        {
            return;
        }

        try
        {
            await _scrapedPrices.AddRangeAsync(observations, ct);
            record(EventCategory.Profile, "price.persist", "pipeline",
                new Dictionary<string, object?> { ["count"] = observations.Count });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Best-effort: persisting price history must never fail the search — but log so a systematically
            // failing price store (a schema/IO problem) is observable instead of silently losing all prices.
            _logger.LogWarning(ex, "Persisting {Count} scraped price(s) failed", observations.Count);
        }
    }

    /// <summary>A price scraped off a rendered store page, with the source line for token matching.</summary>
    private readonly record struct BrowserPrice(decimal Price, string Currency, string Line, string Store, string Url);

    private static bool HasPrice(ProductModel m) => m.Offers.Any(o => o.Price is not null);

    private static async Task<IReadOnlyList<CatalogProduct>> SafeCatalog(
        ContextDevProvider ctx, string domain, ILogger logger, CancellationToken ct)
    {
        try
        {
            // Metered through the ambient per-job observer — the store-catalogue crawl runs on its
            // own provider instance, invisible to the AgentFactory's wiring (see AmbientApiObserver).
            return await Daleel.Core.Observability.ApiCallTimer.TimeAsync(
                Daleel.Core.Observability.AmbientApiObserver.Observer,
                Daleel.Core.Observability.AmbientApiObserver.Estimator ?? new Daleel.Core.Observability.CostEstimator(),
                "Context.dev", "catalog/extract", domain,
                () => ctx.ExtractProductsAsync(domain, maxProducts: 12, timeoutMs: CatalogTimeoutMs, cancellationToken: ct));
        }
        catch (OperationCanceledException) { throw; } // genuine cancellation/timeout must propagate
        catch (Exception ex)
        {
            // Any failure (incl. the phase-cap timeout) just means "no catalogue for this store" — it must
            // never fail the enrichment, which has already produced a usable result. Log so a consistently
            // failing catalogue provider (e.g. Context.dev 401) doesn't look like "stores have no catalogue".
            logger.LogDebug(ex, "Catalogue extraction failed for {Domain}", domain);
            return Array.Empty<CatalogProduct>();
        }
    }

    /// <summary>Best catalogue product for an item by shared significant tokens (brand/model/SKU).</summary>
    private static CatalogProduct? BestCatalogMatch(ProductModel m, List<CatalogProduct> pool)
    {
        var want = Tokens($"{m.Brand} {m.Model} {m.Name}");
        if (want.Count == 0)
        {
            return null;
        }

        CatalogProduct? best = null;
        var bestScore = 0;
        var bestHave = 0;
        foreach (var c in pool)
        {
            var have = Tokens($"{c.Name} {c.Sku} {c.Category}");
            var score = have.Count(want.Contains);
            if (score > bestScore)
            {
                best = c;
                bestScore = score;
                bestHave = have.Count;
            }
        }

        return IsStrongMatch(bestScore, want.Count, bestHave) ? best : null;
    }

    /// <summary>
    /// True when <paramref name="shared"/> tokens cover at least <see cref="MinMatchRatio"/> of the
    /// SMALLER of the two token sets (and clear the <see cref="MinMatchTokens"/> floor). Percentage-of-
    /// shorter so a long catalogue title and a short query still match when the short side is fully
    /// covered, while a lone incidental shared word never attributes a price.
    /// </summary>
    private static bool IsStrongMatch(int shared, int wantCount, int haveCount)
    {
        if (shared < MinMatchTokens || wantCount == 0 || haveCount == 0)
        {
            return false;
        }

        var shorter = Math.Min(wantCount, haveCount);
        return shared >= (int)Math.Ceiling(MinMatchRatio * shorter);
    }

    private static readonly char[] TokenSeparators = " \t\r\n-_/\\|,.()[]{}،:;\"'".ToCharArray();

    private static HashSet<string> Tokens(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return set;
        }

        foreach (var t in text.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (t.Length >= 3)
            {
                set.Add(t);
            }
        }

        return set;
    }

    private static string? DomainOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var s = url.Trim();
        if (!s.Contains("://"))
        {
            s = "https://" + s;
        }

        if (!Uri.TryCreate(s, UriKind.Absolute, out var u))
        {
            return null;
        }

        return u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
    }

    /// <summary>
    /// The page to scrape for specs: prefer the brand's OWN product page (an offer classified as a
    /// brand page — authoritative, region-correct specs), then the cheapest offer, then any offer.
    /// </summary>
    public static string? OfficialOrCheapestUrl(ProductModel m) =>
        m.Offers.FirstOrDefault(o => o.SourceType == ResultType.BrandPage && !string.IsNullOrWhiteSpace(o.Url))?.Url
        ?? m.Offers.FirstOrDefault(o => o.IsLowest && !string.IsNullOrWhiteSpace(o.Url))?.Url
        ?? m.Offers.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o.Url))?.Url;

    private async Task<ProductProfile?> SafeGet(string key, CancellationToken ct)
    {
        try { return await _repo.GetByKeyAsync(key, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading product profile {Key} failed; treating as a miss", key);
            return null;
        }
    }

    private async Task SafeUpsert(ProductProfile profile, CancellationToken ct)
    {
        try { await _repo.UpsertAsync(profile, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Best-effort: saving a deep-dive must never fail the search — but a consistently failing upsert
            // means deep-dives are never cached, so log at Warning rather than swallowing silently.
            _logger.LogWarning(ex, "Saving product deep-dive {Key} failed", profile.NameKey);
        }
    }
}
