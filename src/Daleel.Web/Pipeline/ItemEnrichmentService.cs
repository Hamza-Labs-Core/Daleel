using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Core.Pricing;
using Daleel.Search.Abstractions;
using Daleel.Search.Http;
using Daleel.Search.Providers;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Pipeline.Enrichment;
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

    /// <summary>Phase 0 as a unit: brand-DB read-through over the whole list. Null = nothing changed.</summary>
    Task<List<ProductModel>?> FillFromBrandDatabaseUnitAsync(List<ProductModel> models, string? geo, CancellationToken ct);

    /// <summary>Phase 0.5 as a unit: vision identification, internal candidate selection + caps preserved. Null = nothing changed.</summary>
    Task<List<ProductModel>?> IdentifyViaVisionUnitAsync(List<ProductModel> models, string? geo, CancellationToken ct);

    /// <summary>Phases 1–3 for ONE item: fresh-profile reuse, else official-page scrape + ProductProfile upsert. Null = unchanged.</summary>
    Task<ProductModel?> DeepDiveItemAsync(AgentService agent, ProductModel item, CancellationToken ct);

    /// <summary>Phase 4 for ONE store domain over the whole list (inline Context.dev extract + browser fallback + persist observations + match; entryUrl = the DISCOVERED page, second-chance harvested when the domain pass creates nothing). Models null = unchanged. Gate = the per-store diagnostic (which gate this domain hit) for the admin timeline.</summary>
    Task<(List<ProductModel>? Models, int Priced, IReadOnlyList<string> Created, CatalogGate Gate)> AttachCatalogForDomainAsync(AgentService agent, List<ProductModel> models, string domain, string? storeName, string? geo, string? searchId, string? query, string? entryUrl, CancellationToken ct, bool skipVendorCatalog = false);

    /// <summary>Match ALREADY-PERSISTED ScrapedPrice rows for a domain/store (e.g. drained edge results) into the models — no network, no vendor spend. Models null = unchanged.</summary>
    Task<(List<ProductModel>? Models, int Priced, IReadOnlyList<string> Created)> AttachScrapedPricesAsync(List<ProductModel> models, string domain, string? storeName, string? query, CancellationToken ct);

    /// <summary>Phase 6 for ONE item: LLM-extract its photos from its own store/brand page (primary
    /// first). Empty = none found.</summary>
    Task<IReadOnlyList<string>> FindImageForItemAsync(AgentService agent, ProductModel item, CancellationToken ct);

    /// <summary>Phase 7 as a unit: condition backfill over the whole list. Null = unchanged.</summary>
    Task<List<ProductModel>?> BackfillConditionsUnitAsync(List<ProductModel> models, CancellationToken ct);

    /// <summary>Store domains Phase 4 would crawl for this result (pure selection, no I/O).</summary>
    IReadOnlyList<(string Domain, string? StoreName, string? EntryUrl)> SelectCatalogDomains(ProductSearchResult products);

    /// <summary>Brands Phase 5 would harvest from Context.dev for this result (pure selection, no I/O; every surfaced brand, cost/TTL-bounded).</summary>
    IReadOnlyList<string> SelectBrandsForHarvest(ProductSearchResult products);
}

public sealed class ItemEnrichmentService : IItemEnrichmentService
{
    /// <summary>
    /// Items eligible for the paid phases (page scrapes, image search) per run. UNCAPPED by default —
    /// every discovered model is eligible; each paid phase carries its own wall-clock budget and keeps
    /// partial results, so TIME (and the per-job cost cap) is the limiter, never a count. Restrainable
    /// per environment via <c>PIPELINE_MAX_ITEMS</c> (<see cref="PipelineLimits"/>).
    /// </summary>
    private static int MaxItems => PipelineLimits.MaxItems;

    /// <summary>An item is "thin" (worth scraping) when it has fewer than this many specs.</summary>
    private const int ThinSpecThreshold = 3;

    /// <summary>Cap on a saved detail blob (entity column is 8000).</summary>
    private const int MaxDetailChars = 4000;

    /// <summary>Per-catalogue crawl budget — kept under the background enrichment timeout.</summary>
    private const int CatalogTimeoutMs = 30_000;

    /// <summary>
    /// How far back <see cref="AttachScrapedPricesAsync"/> trusts a persisted observation (e.g. a
    /// drained edge-scrape result) as a current price. Prices drift, so an older row is history to
    /// chart, not an offer to attach.
    /// </summary>
    private const int ScrapedPriceReuseWindowMinutes = 60;

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
    private readonly Storage.IR2StorageService? _r2;
    private readonly IBrandRepository _brands;
    private readonly IBrandModelRepository _brandModels;
    private readonly Identification.IProductIdentifier _identifier;
    private readonly IScrapedPriceRepository _scrapedPrices;
    private readonly IBrandCatalogService _brandCatalog;
    private readonly ILogger<ItemEnrichmentService> _logger;
    private readonly Services.IProviderApi _providers;
    private readonly Data.ISiteSearchProfileRepository? _siteProfiles;

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
        ILogger<ItemEnrichmentService> logger,
        Services.IProviderApi? providers = null,
        Storage.IR2StorageService? r2 = null,
        Data.ISiteSearchProfileRepository? siteProfiles = null)
    {
        _siteProfiles = siteProfiles;
        _repo = repo;
        _options = options;
        _factory = factory;
        _r2 = r2;
        _scrapedPrices = scrapedPrices;
        _brandCatalog = brandCatalog;
        _brands = brands;
        _brandModels = brandModels;
        _identifier = identifier;
        _logger = logger;
        // Optional so existing test wiring keeps working; production DI always supplies the gateway.
        _providers = providers ?? new Services.ProviderApi(factory);
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
        var (visionFills, _) = await IdentifyViaVisionAsync(models, products.Geo, progress, Record, ct);

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
            // No scrape-count cap: every thin item is a candidate — the scrape phase's own wall-clock
            // budget decides how many actually run, and it keeps whatever finished.
            if (url is not null && m.Specs.Count < ThinSpecThreshold)
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
                var content = page is null ? null : TruncateDetail(page.Content);
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
                Name = r.m.Name, Brand = r.m.Brand, Model = r.m.Model, Sku = r.m.Sku, NameKey = r.key,
                Details = r.content, SourceUrl = r.url, LastRefreshed = now
            }, ct);
        }

        // Merge any fresh/cached spec details into the working models.
        var withSpecs = models
            .Select((m, idx) => enriched.TryGetValue(idx, out var detail) ? WithDetail(m, detail) : m)
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

        // Phase 6 — LAST-RESORT image scrape for whatever the store catalogues, brand sites, and
        // product database all failed to carry a photo for: fetch the item's OWN offer page and read
        // its og:image. A failed scrape just leaves the placeholder — never a guessed thumbnail.
        var imagesFound = 0;
        var imageAttempts = 0;
        // The bound caps per-pass scrape ATTEMPTS, not successes — on a large grid of imageless items,
        // counting only hits would keep fetching after failed lookup with no ceiling. Items past the
        // bound stay imageless this pass and are re-attempted when the drain re-runs.
        for (var i = 0; i < pricedModels.Count && imageAttempts < MaxImageLookups; i++)
        {
            var m = pricedModels[i];
            if (!string.IsNullOrWhiteSpace(m.ImageUrl))
            {
                continue;
            }

            imageAttempts++;

            var gallery = await FindImageForItemAsync(agent, m, ct);
            if (gallery.Count == 0)
            {
                continue;
            }

            var (primary, others) = SplitGallery(gallery, m.Images);
            pricedModels[i] = m with { ImageUrl = primary, Images = others };
            imagesFound++;
            Record(EventCategory.Extract, "item.image", "scrape",
                new Dictionary<string, object?> { ["item"] = m.Name, ["images"] = gallery.Count });
        }

        if (imagesFound > 0)
        {
            progress($"Found product images for {imagesFound} item(s).");
        }

        // Phase 7 — EDGE CONDITION BACKFILL (classify-worker): models whose offers all lack a
        // condition get a commodity used/refurbished label from their listing names. Advisory and
        // conservative: only high-confidence non-"new" labels apply ("new" is the default reading
        // of an unlabeled listing, so stamping it adds nothing), and any failure changes nothing.
        var conditionsLabeled = 0;
        if (_providers.HasEdgeClassify)
        {
            conditionsLabeled = await BackfillConditionsAsync(pricedModels, ct);
            if (conditionsLabeled > 0)
            {
                progress($"Labeled condition on {conditionsLabeled} item(s).");
            }
        }

        var touched = enriched.Count + priced + imagesFound + visionFills
                      + dbImages + dbPrices + dbSpecs
                      + storeImages + storeSpecs
                      + brandImages + brandPrices + brandSpecs
                      + conditionsLabeled;
        if (touched == 0)
        {
            return new ItemEnrichmentResult(null, events);
        }

        progress(
            $"Deep-dived {enriched.Count} item(s) — {fresh} new, {enriched.Count - fresh} reused" +
            (priced > 0 ? $"; added live prices to {priced}." : "."));
        return new ItemEnrichmentResult(products with { Models = pricedModels }, events);
    }

    // The *UnitAsync members below re-expose the phases one queued work item at a time for the
    // durable enrichment consumer. Units record no PipelineEvents (the consumer meters paid calls
    // via AmbientApiObserver); progress lines become _logger calls. Cancellation of the caller's
    // token always surfaces as OperationCanceledException — the consumer owns retries and timeouts.

    public async Task<List<ProductModel>?> FillFromBrandDatabaseUnitAsync(
        List<ProductModel> models, string? geo, CancellationToken ct)
    {
        var (filled, images, prices, specs) = await FillFromBrandDatabaseAsync(models, geo, ct);
        if (images + prices + specs == 0)
        {
            return null;
        }

        _logger.LogInformation("Matched {Count} detail(s) from the product database", images + prices + specs);
        return filled;
    }

    public async Task<List<ProductModel>?> IdentifyViaVisionUnitAsync(
        List<ProductModel> models, string? geo, CancellationToken ct)
    {
        var (fills, changed) = await IdentifyViaVisionAsync(models, geo, progress: null, record: null, ct);
        if (fills > 0)
        {
            _logger.LogInformation("Identified {Count} item(s) by product photo", fills);
        }

        // An identification can change a model (canonical name) without a countable fill.
        return changed ? models : null;
    }

    public async Task<ProductModel?> DeepDiveItemAsync(AgentService agent, ProductModel item, CancellationToken ct)
    {
        var now = _options.Now();
        var key = ProductProfile.KeyFor(item.Brand, item.Model, item.Name);
        if (key.Length == 0)
        {
            return null;
        }

        var saved = await SafeGet(key, ct);
        if (saved is { } s && !s.IsStale(now, _options.Ttl) && !string.IsNullOrWhiteSpace(s.Details))
        {
            return WithDetail(item, s.Details!);
        }

        // Same scrape gate as the orchestrated phase: a URL to read and a thin item worth reading for.
        var url = OfficialOrCheapestUrl(item);
        if (url is null || item.Specs.Count >= ThinSpecThreshold)
        {
            return null;
        }

        _logger.LogInformation("Fetching official specs for {Item}", item.Name);
        var page = await agent.ReadPageAsync(url, ct);
        var content = page is null ? null : TruncateDetail(page.Content);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        await SafeUpsert(new ProductProfile
        {
            Name = item.Name, Brand = item.Brand, Model = item.Model, Sku = item.Sku, NameKey = key,
            Details = content, SourceUrl = url, LastRefreshed = now
        }, ct);
        return WithDetail(item, content!);
    }

    public async Task<(List<ProductModel>? Models, int Priced, IReadOnlyList<string> Created, CatalogGate Gate)> AttachCatalogForDomainAsync(
        AgentService agent, List<ProductModel> models, string domain, string? storeName, string? geo,
        string? searchId, string? query, string? entryUrl, CancellationToken ct,
        bool skipVendorCatalog = false)
    {
        // geo/searchId ride on the queue contract; this unit needs neither (events are dropped here).
        // The gap gate only short-circuits when there's no query to discover NEW models for — a store
        // catalogue is a first-class product source, not just a gap-filler for existing items.
        if (SignificantQueryTokens(query, geo).Count == 0 &&
            !models.Any(m => !HasPrice(m) || string.IsNullOrWhiteSpace(m.ImageUrl) || m.Specs.Count < ThinSpecThreshold))
        {
            return (null, 0, Array.Empty<string>(), CatalogGate.NoQueryGap);
        }

        // VALIDATE THE SITE BEFORE SENDING IT. Google Places gives many stores no website, and any
        // hostname derived from a store's display name is fiction. A crawl of a host that does not
        // resolve bills exactly the same as a real one and comes back empty — so pay one free DNS
        // lookup here instead of a catalogue extract plus a browser scrape. (IsSafePublicUrlAsync
        // resolves DNS: false for an unresolvable host as well as for an internal one.)
        if (!await SsrfGuard.IsSafePublicUrlAsync($"https://{domain}", ct).ConfigureAwait(false))
        {
            _logger.LogDebug("Skipping catalogue crawl for unsafe or unresolvable domain {Domain}", domain);
            return (null, 0, Array.Empty<string>(), CatalogGate.Unresolvable);
        }

        // No internal phase-budget CancellationTokenSource (unlike AttachCatalogDataAsync's
        // catalogCts): the queue consumer owns this unit's timeout, so cancelling ct must surface
        // as a plain OperationCanceledException instead of faulting a shared multi-domain phase.
        // skipVendorCatalog: the caller already has the broad domain catalogue covered (an edge
        // submit is in flight and the drain will attach its rows) — this pass exists only for the
        // QUERY-scoped harvest below, which is what actually creates query-relevant grid items.
        var pool = new List<CatalogProduct>();
        if (_providers.HasScraper && !skipVendorCatalog)
        {
            _logger.LogInformation("Reading the {Domain} catalogue for live prices", domain);
            pool = (await SafeCatalog(_providers, domain, _logger, ct)).ToList();
        }

        // WHAT to search the store for. CatalogQueryFor derives its query from an existing model's
        // gap — with ZERO models there is no gap, it returns empty, and ProductSearchUrl degrades to
        // the store ROOT: a homepage of category links, not products. Seeding a grid from scratch (the
        // whole point when a search found stores but no items) must search the store for what the USER
        // asked for instead.
        // The RAW user query is the wrong thing to type into a STORE's search box: store engines
        // AND-match terms, and geo/filler words ("electric kettle IN AMMAN") match no product title,
        // so the store's own search returns its no-results page — 100k chars of navigation with zero
        // products (QA: extra.com, 108,765 chars, 0 extracted). Search the store for the significant
        // product tokens only; the search as a whole is already scoped to the market.
        var harvestQuery = CatalogQueryFor(models) is { Length: > 0 } modelQuery
            ? modelQuery
            : string.Join(' ', SignificantQueryTokens(query, geo));

        // Browser fallback fires exactly when the orchestrated path's would: nothing PRICED came
        // out of the structured extract for this domain (including the no-scraper case). The LLM sink
        // collects everything its last-resort extractor named — including NAME-ONLY products, which a
        // BrowserPrice cannot carry but a seeded model happily can ("see price on store site").
        var llmSeed = new List<ProductListing>();
        var browserPrices = pool.All(p => p.Price is null)
            ? await HarvestViaBrowserAsync(agent, new[] { domain }, harvestQuery, geo, record: null, ct, llmSeed)
            : Array.Empty<BrowserPrice>();

        if (pool.Count == 0 && browserPrices.Count == 0 && llmSeed.Count == 0)
        {
            return (null, 0, Array.Empty<string>(), CatalogGate.EmptyCrawl);
        }

        var (updated, priced, images, specsFilled, observations) =
            AttachPoolToModels(models, pool, browserPrices, storeName);
        await PersistObservations(observations, record: null, ct);

        // Catalogue entries that matched nothing but ARE what the user searched for become NEW
        // models — this is how a deep local store's inventory reaches the grid at all (a 184-item
        // catalogue used to contribute zero items unless web extraction had already named them).
        // The LLM-named products join them, priced or not: an unpriced item the shopper can still
        // click through to is worth infinitely more than an empty grid.
        // Trust levels, learned the hard way (QA: 44 "products" of "Sale price38.990 JOD" junk):
        // - vendor POOL (domain-wide crawl, structured names) creates models behind the query gate;
        // - the LLM SEED (structured extraction from the store's own query-scoped search page)
        //   creates models WITHOUT the gate — the store's engine already matched them, in the
        //   store's language, and the LLM produced a real name;
        // - raw REGEX price lines create NOTHING, ever: a line-with-a-price is "Sale price38.990
        //   JOD" or a shipping banner as often as a product, and a junk model self-propagates (its
        //   gap-query sends stores searching for the junk). They only ATTACH prices to existing
        //   models via AttachPoolToModels above.
        var (withNew, created) = AppendCatalogDiscoveries(
            updated,
            pool.Select(c => (c.Name, c.Price, c.Currency, c.Url, c.ImageUrl, (string?)null, Indicative: false)),
            storeName, query, geo);
        var (withHarvested, harvestedCreated) = AppendCatalogDiscoveries(
            withNew,
            llmSeed
                .Where(l => !string.IsNullOrWhiteSpace(l.Name))
                .Select(l => (l.Name, l.Price, l.Currency, l.Url, l.ImageUrl, l.Availability, Indicative: l.Price is null)),
            storeName, query, geo, fromQueryScopedPage: true);
        withNew = withHarvested;
        created = created.Concat(harvestedCreated).ToList();

        // Second chance on the DISCOVERED page: a general store's root crawl can return inventory
        // that has nothing to do with the query (jo-cell.com root is phones; its espresso machines
        // live under /collections/espresso-machines — the very url Google handed us). When the
        // domain-level pass created nothing, render/extract that page directly.
        if (created.Count == 0 && !string.IsNullOrWhiteSpace(entryUrl) &&
            SignificantQueryTokens(query).Count > 0)
        {
            var entryPrices = await HarvestPageAsync(agent, domain, entryUrl!, ct);
            if (entryPrices.Count > 0)
            {
                // Regex lines: price attachment only — model creation is reserved for structured
                // names (vendor pool, LLM seed); see the trust-level note above.
                (withNew, var entryPriced, _, _, _) =
                    AttachPoolToModels(withNew, new List<CatalogProduct>(), entryPrices, storeName);
                priced += entryPriced;
            }
        }

        // Past the empty-crawl gate, so at least one source returned something: the gate now turns on
        // whether that produced a NEW grid item (ProducedItems) or only matched existing ones (MatchedNoNewItems).
        var gate = CatalogGateClassifier.Classify(new CatalogAttemptSignals(
            ResolvableDomain: true, HasQueryGap: true,
            VendorPoolCount: pool.Count, BrowserPriceCount: browserPrices.Count,
            LlmSeedCount: llmSeed.Count, ItemsCreated: created.Count));

        if (priced + images + specsFilled == 0 && created.Count == 0)
        {
            return (null, 0, Array.Empty<string>(), gate);
        }

        _logger.LogInformation(
            "Store catalogue {Domain} matched {Priced} price(s), {Images} image(s), {Specs} spec set(s), " +
            "added {Created} new model(s)",
            domain, priced, images, specsFilled, created.Count);
        return (withNew, priced, created, gate);
    }

    public async Task<(List<ProductModel>? Models, int Priced, IReadOnlyList<string> Created)> AttachScrapedPricesAsync(
        List<ProductModel> models, string domain, string? storeName, string? query, CancellationToken ct)
    {
        var since = _options.Now().AddMinutes(-ScrapedPriceReuseWindowMinutes);

        // Persisted rows may be keyed by either identity — the scrape paths store the source domain,
        // saved stores a display name — so read both when they differ (distinct keys can't collide).
        var rows = new List<ScrapedPrice>(await _scrapedPrices.ListRecentByStoreAsync(domain, since, ct));
        if (!string.IsNullOrWhiteSpace(storeName) &&
            !string.Equals(storeName, domain, StringComparison.OrdinalIgnoreCase))
        {
            rows.AddRange(await _scrapedPrices.ListRecentByStoreAsync(storeName!, since, ct));
        }

        // The shape the shared matcher consumes: name/price/currency/sourceUrl (+ the crawl's image when
        // it stored one). This branch marks the offer indicative, which is right here: an up-to-an-hour-old
        // observation is a lead to verify at the store, not a live quote.
        var pool = rows
            .Where(r => r.Price is not null && !string.IsNullOrWhiteSpace(r.ProductName))
            .Select(r => new BrowserPrice(
                r.Price!.Value, r.Currency ?? string.Empty, r.ProductName,
                string.IsNullOrWhiteSpace(r.StoreName) ? domain : r.StoreName, r.SourceUrl ?? string.Empty,
                string.IsNullOrWhiteSpace(r.ImageUrl) ? null : r.ImageUrl))
            .ToList();

        // Discoveries are broader than the priced pool: a store page that shows no price still yields
        // a NAMED, PHOTOGRAPHED item ("See price on store site" is a fine card; a grey placeholder is
        // not — QA: every fan/diaper card). An unpriced row rides along when it carries an image; an
        // unpriced, imageless row adds nothing over the live seed path and stays out.
        var discoveries = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.ProductName) &&
                        (r.Price is not null || !string.IsNullOrWhiteSpace(r.ImageUrl)))
            .Select(r => (Name: r.ProductName, r.Price, r.Currency, Url: r.SourceUrl,
                          ImageUrl: string.IsNullOrWhiteSpace(r.ImageUrl) ? null : r.ImageUrl,
                          r.Availability,
                          Indicative: true))
            .ToList();
        if (pool.Count == 0 && discoveries.Count == 0)
        {
            return (null, 0, Array.Empty<string>());
        }

        // Observations from the matcher are DISCARDED: these prices came out of the history table,
        // and persisting them again would duplicate the append-only series.
        var (updated, priced, _, _, _) = AttachPoolToModels(models, new List<CatalogProduct>(), pool, storeName);

        // Drained edge catalogues create models too — indicative offers, like every drained price. When
        // the crawl stored a photo on the row, the new model carries it (the site-crawl's own image),
        // instead of landing imageless and needing a fallback page-scrape that may find nothing.
        var (withNew, created) = AppendCatalogDiscoveries(
            updated ?? models,
            discoveries,
            storeName, query);
        return priced > 0 || created.Count > 0
            ? (withNew, priced, created)
            : (null, 0, Array.Empty<string>());
    }

    /// <summary>
    /// The item's photos come from the SAME LLM detail-extraction the product-detail crawl uses — not a
    /// paid image search, and not brittle regex as the primary source. Fetch the item's own offer/brand
    /// page and let <see cref="AgentService.ExtractProductDetailAsync"/> read the whole record and pick
    /// the product images (primary first): it ignores logos/promos, resolves galleries, and understands
    /// the page far better than pattern-matching. Returned primary-first — the card takes the first, the
    /// detail panel shows the gallery. Only when the LLM finds nothing (the photo lived solely in the
    /// HTML head as <c>og:image</c>, or in a lazy-load attribute the markdown render drops) do we fall
    /// back to reading the raw HTML for the store's declared image(s). No offer URL, no scraper, or
    /// nothing found → empty: an honest placeholder, never a guessed thumbnail.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindImageForItemAsync(AgentService agent, ProductModel item, CancellationToken ct)
    {
        if (ImageSourceUrl(item) is not { } url || !_providers.HasScraper)
        {
            return Array.Empty<string>();
        }

        // Best-effort: the page fetch runs through the Context.dev branch, which RE-THROWS on a bad URL
        // (404/DNS/TLS). One dead offer link must never fault the image unit — that would discard the
        // whole batch's found images and, after retries, kill image lookups for the rest of the grid.
        // Degrade to "no image for this item" instead (mirrors VerifyPageHandler's guarded fetch).
        try
        {
            // Same steps as ProductDetailActivities: render the page, then LLM-extract the full record
            // and take its product images. The LLM reads the markdown the way it reads every detail page.
            var page = await _providers.ScrapePageAsync(url, ct: ct).ConfigureAwait(false);
            if (page is not null && !string.IsNullOrWhiteSpace(page.Content))
            {
                var listing = new ProductListing { Url = url, Name = item.Name, Brand = item.Brand, Model = item.Model };
                var (_, detail) = await agent.ExtractProductDetailAsync(
                    page.Content, listing, GeoProfiles.ResolveOrDefault(null), ct).ConfigureAwait(false);
                if (detail is { Images.Count: > 0 })
                {
                    return detail.Images;
                }
            }

            // Backstop: the photo may live only in the HTML head (og:image) or a lazy-load attribute the
            // markdown drops — read the raw HTML for the store's declared canonical image(s).
            var html = await _providers.ScrapePageAsync(url, ScrapeFormat.Html, ct).ConfigureAwait(false);
            return html is not null && !string.IsNullOrWhiteSpace(html.Content)
                ? OfferVerificationHandler.ExtractImages(html.Content)
                : Array.Empty<string>();
        }
        catch (OperationCanceledException)
        {
            throw; // genuine cancellation (workflow deadline / cost cap) must propagate
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Image scrape failed for {Url}", url);
            return Array.Empty<string>();
        }
    }

    /// <summary>Merge a freshly-found gallery with the item's existing images: primary first, deduped,
    /// order preserved.</summary>
    private static (string Primary, List<string> Others) SplitGallery(IReadOnlyList<string> gallery, IReadOnlyList<string> existing)
    {
        var merged = gallery.Concat(existing)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (merged[0], merged.Skip(1).ToList());
    }

    /// <summary>The page to scrape for this item's photo: its first offer URL (the store's product
    /// page), else the brand site — the same pages the crawl already trusts.</summary>
    private static string? ImageSourceUrl(ProductModel m) =>
        m.Offers.Select(o => o.Url).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u))
        ?? (string.IsNullOrWhiteSpace(m.BrandSiteUrl) ? m.BrandRegionalUrl : m.BrandSiteUrl);

    public async Task<List<ProductModel>?> BackfillConditionsUnitAsync(List<ProductModel> models, CancellationToken ct)
    {
        if (!_providers.HasEdgeClassify)
        {
            return null;
        }

        var applied = await BackfillConditionsAsync(models, ct);
        if (applied == 0)
        {
            return null;
        }

        _logger.LogInformation("Labeled condition on {Count} item(s)", applied);
        return models;
    }

    public IReadOnlyList<(string Domain, string? StoreName, string? EntryUrl)> SelectCatalogDomains(ProductSearchResult products)
    {
        // No site-count cap, mirroring the phase: every distinct store domain is a candidate —
        // budgets decide how many crawls complete. The first store to claim a domain names it.
        // The DISCOVERED url rides along when it points somewhere specific: Google returned
        // jo-cell.com/collections/espresso-machines — crawling the domain ROOT of a general store
        // finds phones, not espresso machines; the deep link is the query-relevant inventory.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selected = new List<(string Domain, string? StoreName, string? EntryUrl)>();
        foreach (var store in products.Stores)
        {
            if (DomainOf(store.Url) is not { } domain || !seen.Add(domain))
            {
                continue;
            }

            var entryUrl = Uri.TryCreate(store.Url, UriKind.Absolute, out var u) && u.AbsolutePath.Length > 1
                ? store.Url
                : null;
            selected.Add((domain, string.IsNullOrWhiteSpace(store.Name) ? null : store.Name, entryUrl));
        }

        return selected;
    }

    public IReadOnlyList<string> SelectBrandsForHarvest(ProductSearchResult products) =>
        products.Brands
            .Select(b => b.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // Every surfaced brand gets its Context.dev catalogue harvested — the brand APIs are the
            // canonical brand-product source (no lucky top-2). Uncapped by default like the store-catalogue
            // and item fan-outs; the grid's brand set is naturally small and the per-(brand,level) TTL gate
            // stops repeat searches re-billing. Restrainable via PIPELINE_MAX_BRAND_CATALOGS since each
            // harvest is a paid crawl (see PipelineLimits.MaxBrandCatalogs).
            .Take(PipelineLimits.MaxBrandCatalogs)
            .ToList();

    /// <summary>Detail blobs are capped to the entity column budget wherever a page is scraped.</summary>
    private static string TruncateDetail(string content) =>
        content.Length <= MaxDetailChars ? content : content[..MaxDetailChars];

    /// <summary>The deep-dive's output shape: the scraped/cached blob rides in Specs["details"].</summary>
    private static ProductModel WithDetail(ProductModel m, string detail)
    {
        var specs = new Dictionary<string, string>(m.Specs) { ["details"] = detail };
        return m with { Specs = specs };
    }

    /// <summary>The lookup text for an item: brand + model, the name standing in for a blank model.</summary>
    private static string BrandModelQuery(ProductModel m) =>
        string.Join(' ', new[]
        {
            m.Brand,
            string.IsNullOrWhiteSpace(m.Model) ? m.Name : m.Model
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>
    /// The browser fallback needs an on-site search query and the per-domain unit runs without the
    /// originating search text — so target the first still-priceless item. An empty query would
    /// render the homepage, whose featured/deal prices get mis-attributed to our items.
    /// </summary>
    private static string CatalogQueryFor(IReadOnlyList<ProductModel> models)
    {
        var gap = models.FirstOrDefault(m => !HasPrice(m)) ?? models.FirstOrDefault();
        return gap is null ? string.Empty : BrandModelQuery(gap);
    }

    /// <summary>Best-effort single-brand harvest: a failed crawl contributes zero models, never a fault.</summary>
    private async Task<int> HarvestOneBrandAsync(string name, CancellationToken ct)
    {
        try
        {
            return await _brandCatalog.HarvestAsync(name, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return 0; }
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

        // No site-count cap: every distinct store domain is a candidate — the phase's catalogCts
        // wall-clock budget below decides how many crawls actually complete, and keeps what finished.
        var domains = SelectCatalogDomains(products).Select(d => d.Domain).ToList();
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
        var pool = new List<CatalogProduct>();
        var browserUnpriced = domains;

        if (_providers.HasScraper)
        {
            progress($"Reading {domains.Count} store catalogue(s) for live prices…");

            var catalogues = await Task.WhenAll(domains.Select(async d =>
            {
                var found = await SafeCatalog(_providers, d, _logger, catalogCts.Token);
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

        var browserPrices = await HarvestViaBrowserAsync(
            agent, browserUnpriced, products.Query, products.Geo, record, catalogCts.Token);

        if (pool.Count == 0 && browserPrices.Count == 0)
        {
            return (models, 0, 0, 0);
        }

        var (updated, priced, images, specsFilled, observations) =
            AttachPoolToModels(models, pool, browserPrices, fallbackStoreName: null);

        // Persist the observations as a timestamped batch — the price history the comparison view reads.
        await PersistObservations(observations, record, ct);

        if (priced + images + specsFilled > 0)
        {
            progress($"Store catalogues matched {priced} price(s), {images} image(s), {specsFilled} spec set(s).");
        }
        return (updated, priced, images, specsFilled);
    }

    /// <summary>
    /// The token-ratio matching core shared by the whole-result phase, the per-domain unit, and the
    /// persisted-price re-attach: every item is matched against the catalogue pool (price when
    /// missing, image, specs), then — still priceless — against the browser price lines. The
    /// observations the matches produce are returned, not persisted: the CALLER decides (the
    /// re-attach path reads rows that already live in the history and must not write them back).
    /// </summary>
    private (List<ProductModel> Models, int Priced, int Images, int Specs, List<ScrapedPrice> Observations)
        AttachPoolToModels(
            List<ProductModel> models, List<CatalogProduct> pool, IReadOnlyList<BrowserPrice> browserPrices,
            string? fallbackStoreName)
    {
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
                        Source = DomainOf(c.Url) ?? fallbackStoreName ?? "Store", SourceType = ResultType.StorePage,
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

                // The scraped-price path (unlike a live rendered line) may carry the crawl's own photo —
                // fill it when this model has none, so an imageless web-discovered item gets the store's image.
                if (string.IsNullOrWhiteSpace(item.ImageUrl) && bp.ImageUrl is { Length: > 0 } bpImg)
                {
                    item = item with { ImageUrl = bpImg };
                    images++;
                }
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

        return (updated, priced, images, specsFilled, observations);
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

        // A Jordan harvest's JOD price must never surface as an offer on a USA search. Attach a
        // brand-DB price only when its currency IS the search market's currency; images and specs
        // are market-agnostic and flow regardless.
        var market = Daleel.Core.Geo.GeoProfiles.ResolveOrDefault(geo);
        var marketCurrency = market.Currency;

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

            // LOCAL-FIRST: rows harvested from the brand's LOCAL site for THIS market get first
            // claim — the in-market storefront price is the one a shopper here actually pays (the
            // currency gate inside FillFromRow still applies). Any-level rows (global/regional/
            // legacy-null = global) then fill whatever gaps remain: image and specs are
            // market-agnostic, and FillFromRow only ever fills blanks, so the second pass can't
            // overwrite what the local row supplied.
            var localRows = rows.Where(r =>
                r.SiteLevel == BrandSiteLevel.Local &&
                string.Equals(r.SiteCountry, market.CountryCode, StringComparison.OrdinalIgnoreCase)).ToList();

            var updated = m;
            var filledImage = false;
            var filledPrice = false;
            var filledSpecs = false;

            var localMatch = localRows.Count > 0 ? BestBrandModelMatch(m, localRows) : null;
            if (localMatch is not null)
            {
                (updated, filledImage, filledPrice, filledSpecs) = FillFromRow(updated, localMatch, marketCurrency, now);
            }

            if (BestBrandModelMatch(m, rows) is { } match && !ReferenceEquals(match, localMatch))
            {
                var (anyFilled, img, price, specs) = FillFromRow(updated, match, marketCurrency, now);
                updated = anyFilled;
                filledImage |= img;
                filledPrice |= price;
                filledSpecs |= specs;
            }

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
    /// Best-effort per item; capped per run. Null callbacks (the unit path) drop the progress line
    /// and the PipelineEvents; Changed reports ANY model mutation, countable fill or not.
    /// </summary>
    private async Task<(int Fills, bool Changed)> IdentifyViaVisionAsync(
        List<ProductModel> models, string? geo, Action<string>? progress,
        Action<string, string, string, IReadOnlyDictionary<string, object?>?>? record, CancellationToken ct)
    {
        var marketCurrency = Daleel.Core.Geo.GeoProfiles.ResolveOrDefault(geo).Currency;
        var now = _options.Now();
        var attempts = 0;
        var fills = 0;
        var changed = false;

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
                    changed = true;
                    if (img || price || specs) fills++;
                }

                record?.Invoke(EventCategory.Extract, "item.identify", "vision",
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
            progress?.Invoke($"Identified {fills} item(s) by product photo.");
        }

        return (fills, changed);
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
        var brands = SelectBrandsForHarvest(products);
        if (brands.Count == 0)
        {
            return;
        }

        foreach (var name in brands)
        {
            ct.ThrowIfCancellationRequested();
            var harvested = await HarvestOneBrandAsync(name, ct);
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
    /// <summary>
    /// classify-worker consumer: batch-labels condition (used/refurbished) for models whose offers
    /// carry none, from listing names alone. Returns how many models were stamped. Best-effort.
    /// </summary>
    private async Task<int> BackfillConditionsAsync(List<ProductModel> models, CancellationToken ct)
    {
        var candidates = models
            .Select((m, idx) => (m, idx))
            .Where(t => t.m.Offers.Count > 0 && t.m.Offers.All(o => o.Condition is null))
            .ToList();
        if (candidates.Count == 0)
        {
            return 0;
        }

        try
        {
            var items = candidates
                .Select(t => (t.idx.ToString(), $"{t.m.Name} {t.m.Model}".Trim()))
                .ToList();
            var verdicts = await _providers.ClassifyTextAsync(
                items, new[] { "new", "used", "refurbished", "unknown" }, ct);

            var applied = 0;
            foreach (var v in verdicts)
            {
                if (v.Label is not ("used" or "refurbished") || v.Confidence < 0.7 ||
                    !int.TryParse(v.Id, out var idx) || idx < 0 || idx >= models.Count)
                {
                    continue;
                }

                var m = models[idx];
                models[idx] = m with
                {
                    Offers = m.Offers
                        .Select(o => o.Condition is null ? o with { Condition = v.Label } : o)
                        .ToList()
                };
                applied++;
            }
            return applied;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return 0; // advisory phase — never fails the enrichment
        }
    }

    /// <param name="llmSink">
    /// When supplied, every product the LLM last resort NAMES is added here — including the unpriced
    /// ones a <see cref="BrowserPrice"/> cannot represent. The caller seeds those as new models.
    /// </param>
    private async Task<IReadOnlyList<BrowserPrice>> HarvestViaBrowserAsync(
        AgentService agent, IReadOnlyList<string> domains, string query, string? geo,
        Action<string, string, string, IReadOnlyDictionary<string, object?>?>? record, CancellationToken ct,
        List<ProductListing>? llmSink = null)
    {
        if (domains.Count == 0)
        {
            return Array.Empty<BrowserPrice>();
        }

        // Resolved once: the LLM last-resort extractor needs a market (currency default). A market we
        // cannot resolve simply skips that fallback rather than guessing.
        var geoProfile = GeoProfiles.Resolve(geo);

        // Learned search interfaces, loaded BEFORE the concurrent fan-out and written back AFTER it —
        // the scoped DbContext must never be touched from inside Task.WhenAll (the same concurrency
        // rule the Blazor circuit crash taught). Missing repo (test wiring) ⇒ probes only.
        var profiles = new Dictionary<string, Data.SiteSearchProfile?>(StringComparer.OrdinalIgnoreCase);
        if (_siteProfiles is not null)
        {
            foreach (var d in domains.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                profiles[d] = await SafeGetSiteProfileAsync(d, ct).ConfigureAwait(false);
            }
        }

        var outcomes = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>(
            StringComparer.OrdinalIgnoreCase);

        var harvested = await Task.WhenAll(domains.Select(async domain =>
        {
            // Candidate search URLs in learned-first order (Shopify /search?q=, WooCommerce /?s=, …).
            // The judge skips error/no-result shells — an HTTP-200 soft-404 used to be "extracted" as
            // an inexplicable zero — so the next convention gets its chance. The extra fetch is paid
            // ONLY when the previous candidate came back unusable.
            ScrapedPage? page = null;
            string? url = null;
            foreach (var candidate in SiteSearch.SiteSearchCandidates.For(
                         domain, query, profiles.GetValueOrDefault(domain)))
            {
                var fetched = await FetchHarvestPageAsync(agent, candidate, ct).ConfigureAwait(false);
                url ??= candidate; // first candidate names the attempt even if everything is unusable
                if (fetched is not null && SiteSearch.HarvestPageJudge.IsUsable(fetched.Content))
                {
                    (page, url) = (fetched, candidate);
                    break;
                }
            }

            var prices = page is null || string.IsNullOrWhiteSpace(page.Content)
                ? new List<BrowserPrice>()
                : PriceParser.Extract(page.Content)
                    .Select(p => new BrowserPrice(p.Price, p.Currency, p.Line, domain, page.Url))
                    .ToList();

            // EDGE EXTRACT FALLBACK (extract-worker): the regex parser found nothing on a page we
            // DID render — hand the markdown to the Workers-AI extract host; structured products
            // with prices become browser-price observations. Strictly additive: only fires when the
            // inline parse came up empty, and its own failure just keeps the empty list.
            var usedEdgeExtract = false;
            if (prices.Count == 0 && page is { Content.Length: > 0 } && _providers.HasEdgeExtract)
            {
                try
                {
                    var extracted = await _providers.ExtractProductsFromContentAsync(page.Content, ct: ct);
                    prices = extracted
                        .Where(p => p.Price is not null && !string.IsNullOrWhiteSpace(p.Name))
                        .Select(p => new BrowserPrice(
                            p.Price!.Value, p.Currency ?? string.Empty, p.Name, domain, p.Url ?? page.Url))
                        .ToList();
                    usedEdgeExtract = prices.Count > 0;
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // best-effort fallback — the empty inline result stands
                }
            }

            // LLM LAST RESORT (OpenRouter, worker-INDEPENDENT): Context.dev gave this domain nothing (an
            // empty products array, or a 400 it could not resolve), the regex parser found nothing, and the
            // edge extract host was empty or unavailable. Hand the markdown we already rendered to the
            // agent's own LLM listing extractor — the ONLY path that still yields items for a store whose
            // catalogue no structured provider can parse. Priced items only (a BrowserPrice needs a price),
            // matching the edge-extract branch above. Best-effort; the empty list stands on any failure.
            var usedLlm = false;
            if (prices.Count == 0 && page is { Content.Length: > 0 } && geoProfile is not null)
            {
                try
                {
                    var llm = await agent.ExtractProductsFromPageAsync(page.Content, query, geoProfile, ct);
                    var named = llm.Where(l => !string.IsNullOrWhiteSpace(l.Name)).ToList();

                    // Everything it NAMED goes to the sink (the caller seeds unpriced items too);
                    // only the priced subset can become BrowserPrice observations. Task.WhenAll runs
                    // the domains concurrently, so the shared sink is locked.
                    if (llmSink is not null && named.Count > 0)
                    {
                        lock (llmSink)
                        {
                            llmSink.AddRange(named);
                        }
                    }

                    prices = named
                        .Where(l => l.Price is not null)
                        .Select(l => new BrowserPrice(
                            l.Price!.Value, l.Currency ?? string.Empty, l.Name, domain, l.Url ?? page.Url))
                        .ToList();
                    usedLlm = named.Count > 0;
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // best-effort fallback — the empty result stands
                }
            }

            _logger.LogInformation(
                "Harvest {Domain}: {Chars} chars via {Provider}, {Prices} price line(s) (edgeExtract={Edge}, llm={Llm})",
                domain, page?.Content?.Length ?? 0, page?.Provider ?? "none", prices.Count, usedEdgeExtract, usedLlm);
            record?.Invoke(EventCategory.Extract, "catalog.browser", page?.Provider ?? "cloudflare-browser",
                new Dictionary<string, object?>
                {
                    ["domain"] = domain, ["url"] = url, ["prices"] = prices.Count,
                    ["edgeExtract"] = usedEdgeExtract, ["llmExtract"] = usedLlm
                });

            // LEARN: a candidate that yielded extractable products is this domain's search interface —
            // persist its template (after the fan-out) so the next harvest leads with it. A learned
            // profile that yielded nothing records a failure so a stale template eventually relearns.
            var yielded = prices.Count > 0 || usedLlm;
            if (yielded && url is not null &&
                SiteSearch.SiteSearchCandidates.TemplateFor(url) is { } template)
            {
                outcomes[domain] = template;
            }
            else if (!yielded && profiles.GetValueOrDefault(domain) is not null)
            {
                outcomes[domain] = null;
            }

            return (IReadOnlyList<BrowserPrice>)prices;
        }));

        // Write the learning back OUTSIDE the concurrent region (single-threaded DbContext rule).
        if (_siteProfiles is not null)
        {
            foreach (var (domain, template) in outcomes)
            {
                await SafeRecordSiteOutcomeAsync(domain, template, ct).ConfigureAwait(false);
            }
        }

        return harvested.SelectMany(h => h).ToList();
    }

    /// <summary>Best-effort profile read: a DB blip must cost the probes, never the harvest.</summary>
    private async Task<Data.SiteSearchProfile?> SafeGetSiteProfileAsync(string domain, CancellationToken ct)
    {
        try { return await _siteProfiles!.GetByDomainAsync(domain, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Site-search profile read failed for {Domain}", domain);
            return null;
        }
    }

    /// <summary>Best-effort learning write: losing a lesson must never fail the harvest that taught it.</summary>
    private async Task SafeRecordSiteOutcomeAsync(string domain, string? template, CancellationToken ct)
    {
        try
        {
            await _siteProfiles!.RecordOutcomeAsync(domain, template, _options.Now(), ct).ConfigureAwait(false);
            if (template is not null)
            {
                _logger.LogInformation("Learned search interface for {Domain}: {Template}", domain, template);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Site-search profile write failed for {Domain}", domain);
        }
    }


    /// <summary>
    /// Fetches one harvest page: server-side markdown FIRST (these storefronts serve their search
    /// grids fine to a plain fetch, and it costs a quarter of a render), browser render as the
    /// fallback for pages that genuinely need JS. The browser-first order was backwards here — a
    /// snapshot can fire before a storefront hydrates its results client-side, returning a JS
    /// skeleton: an empty page we still paid to render (QA: 35 of 40 harvest renders came back
    /// empty, so neither extract fallback ever fired and 40 real store searches seeded 0 items).
    /// </summary>
    private async Task<ScrapedPage?> FetchHarvestPageAsync(AgentService agent, string url, CancellationToken ct)
    {
        ScrapedPage? page = null;
        try
        {
            page = await _providers.ScrapePageAsync(url, ScrapeFormat.Markdown, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { page = null; }

        if (page is null || string.IsNullOrWhiteSpace(page.Content))
        {
            try
            {
                page = await agent.ReadPageAsync(url, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { page = null; }
        }

        await SaveHarvestPageAsync(url, page, ct).ConfigureAwait(false);
        return page;
    }

    /// <summary>
    /// Persists every fetched harvest page to R2 — the save-everything rule the edge results
    /// already follow. A paid fetch that lives only in one method call can't be replayed: when
    /// extraction returns nothing, the page IS the evidence (and the test fixture), and
    /// re-fetching it costs money the first fetch already spent. Best-effort by contract.
    /// </summary>
    private async Task SaveHarvestPageAsync(string url, ScrapedPage? page, CancellationToken ct)
    {
        if (_r2 is not { IsConfigured: true } || page is null || string.IsNullOrWhiteSpace(page.Content))
        {
            return;
        }

        try
        {
            var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "unknown";
            var slug = new string(url.Where(char.IsLetterOrDigit).TakeLast(40).ToArray());
            var key = $"harvest/{_options.Now():yyyyMMdd}/{host}/{slug}.json";
            await _r2.StoreJsonAsync(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    url,
                    fetchedAt = _options.Now(),
                    provider = page.Provider,
                    chars = page.Content!.Length,
                    content = page.Content
                }),
                key, Storage.R2Bucket.Logs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Saving harvest page for {Url} failed (best-effort)", url);
        }
    }

    /// <summary>
    /// Renders and extracts ONE explicit page (the store url discovery actually returned — e.g. a
    /// query-relevant collection page) into browser-price lines. Same parse → edge-extract fallback
    /// chain as <see cref="HarvestViaBrowserAsync"/>; best-effort, empty on any failure.
    /// </summary>
    private async Task<IReadOnlyList<BrowserPrice>> HarvestPageAsync(
        AgentService agent, string domain, string url, CancellationToken ct)
    {
        var page = await FetchHarvestPageAsync(agent, url, ct);
        if (page is null || string.IsNullOrWhiteSpace(page.Content))
        {
            return Array.Empty<BrowserPrice>();
        }

        var prices = PriceParser.Extract(page.Content)
            .Select(p => new BrowserPrice(p.Price, p.Currency, p.Line, domain, page.Url))
            .ToList();
        if (prices.Count == 0 && _providers.HasEdgeExtract)
        {
            try
            {
                var extracted = await _providers.ExtractProductsFromContentAsync(page.Content, ct: ct);
                prices = extracted
                    .Where(p => p.Price is not null && !string.IsNullOrWhiteSpace(p.Name))
                    .Select(p => new BrowserPrice(
                        p.Price!.Value, p.Currency ?? string.Empty, p.Name, domain, p.Url ?? page.Url))
                    .ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // best-effort — the empty parse stands
            }
        }

        return prices;
    }

    // ProductSearchUrl (the hardcoded /search?q= guess) is gone: the gate audit proved that Shopify-only
    // convention 404'd nearly every non-Shopify store — SiteSearchCandidates now tries the learned
    // per-domain template first, then the known platform conventions.

    // Geography words never appear in product names ("best acs in JORDAN"), and the market's own place
    // names ("amman") typed into a store's search box AND-match nothing — the store answers with its
    // no-results page (QA: 61 harvests). Stripping them down to product tokens now lives in the shared
    // QueryScope so the enrichment harvest and the LLM site crawl can never drift apart on it.
    private static List<string> SignificantQueryTokens(string? query, string? geo = null) =>
        QueryScope.SignificantTokens(query, geo).ToList();

    /// <summary>
    /// Singular/plural-tolerant token match, requiring at least TWO query tokens when the query has
    /// them: one shared token proved far too loose in QA — "espresso machine" admitted a fondant
    /// spray gun (via "machine") and espresso CUPS (via "espresso"); requiring both keeps exactly
    /// the machines. Single-token queries keep the single-token bar.
    /// </summary>
    private static bool NameMatchesQuery(string name, IReadOnlyList<string> queryTokens)
    {
        var have = ShortTokens(name); // 2-char product nouns (AC, TV) must be matchable
        if (have.Count == 0)
        {
            return false;
        }

        var matched = queryTokens.Count(q =>
            have.Any(h => string.Equals(NormalizeNoun(h), NormalizeNoun(q), StringComparison.OrdinalIgnoreCase)));
        return matched >= Math.Min(2, queryTokens.Count);
    }

    /// <summary>
    /// Plural trim + the one commerce synonym pair that matters here: "maker" and "machine" name the
    /// same product category across appliances (coffee/espresso/ice/pasta maker ≡ machine), and a
    /// DeLonghi "Espresso Maker" must count as a hit for an "espresso machine" query.
    /// </summary>
    private static string NormalizeNoun(string token)
    {
        var stem = token.TrimEnd('s', 'S');
        return stem.Equals("maker", StringComparison.OrdinalIgnoreCase) ? "machine" : stem;
    }

    /// <summary>
    /// Numeric variant tokens of a product name ("2", "1.5", "12000") — the discriminators that
    /// separate a 2-ton AC from its 3-ton sibling. Text-token overlap between such siblings is
    /// near-total, so ONLY these numbers can tell them apart.
    /// </summary>
    internal static HashSet<string> VariantNumbers(string name) =>
        name.Split(' ', '-', '/', '،', ',', '(', ')')
            .Select(tok => tok.Trim().TrimEnd('"'))
            .Where(tok => tok.Length > 0 && tok.All(c => char.IsDigit(c) || c == '.') && tok.Any(char.IsDigit))
            .Select(NormalizeDecimal)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// "2.0" ≡ "2" and "2.50" ≡ "2.5" — but only trailing zeros of a FRACTION collapse. A naive
    /// TrimEnd('.','0') also ate integer zeros ("100" → "1", "10" → "1"), collapsing a 100L and a
    /// 10L heater into the same variant. Only strip zeros after a decimal point, then a bare dot.
    /// </summary>
    private static string NormalizeDecimal(string token)
    {
        if (!token.Contains('.'))
        {
            return token; // pure integer: every digit is significant
        }

        var trimmed = token.TrimEnd('0').TrimEnd('.');
        return trimmed.Length > 0 ? trimmed : token;
    }

    /// <summary>
    /// True when two names carry DISAGREEING variant numbers — both have numbers, none shared.
    /// Such names are different SKUs of the same line (2 vs 3 ton), never the same product: a
    /// match between them mis-attributes prices and swallows distinct variants.
    /// </summary>
    internal static bool VariantsDisagree(string a, string b)
    {
        var va = VariantNumbers(a);
        var vb = VariantNumbers(b);
        return va.Count > 0 && vb.Count > 0 && !va.Overlaps(vb);
    }

    /// <summary>True when the entry already decorated an existing model (the matcher's own bar).</summary>
    private static bool MatchedByAnyModel(string name, IReadOnlyList<ProductModel> models)
    {
        var have = Tokens(name);
        if (have.Count == 0)
        {
            return false;
        }

        foreach (var m in models)
        {
            var identity = $"{m.Brand} {m.Model} {m.Name}";
            if (VariantsDisagree(name, identity))
            {
                continue; // a disagreeing SKU is a sibling, not this model
            }

            var want = Tokens(identity);
            if (want.Count == 0)
            {
                continue;
            }

            var shared = want.Count(have.Contains);
            if (IsStrongMatch(shared, want.Count, have.Count))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Appends catalogue entries that matched NO existing model but ARE query-relevant as new models.
    /// This is what lets a deep store catalogue put products on the grid instead of only decorating
    /// items web extraction happened to name. Deduped by normalized name; uncapped by design — the
    /// relevance gate and the store's own inventory are the bounds.
    /// </summary>
    /// <summary>
    /// Maps a brand's harvested <see cref="BrandModel"/> rows to the catalogue-entry shape
    /// <see cref="AppendCatalogDiscoveries"/> turns into grid products — so a discovered brand's
    /// catalogue becomes a GRID SOURCE (each model a product), not just enrichment of items already
    /// found. Relevance + dedup are AppendCatalogDiscoveries' job, so a "coffee machine" search only
    /// gains the brand's coffee machines. The price is the brand-site LOCAL price, marked indicative
    /// (a lead to verify at a store, like every non-store-offer price), never a live local offer.
    /// </summary>
    public static IEnumerable<(string Name, decimal? Price, string? Currency, string? Url, string? Image, string? Availability, bool Indicative)>
        BrandCatalogEntries(IEnumerable<BrandModel> models) =>
        models
            .Where(m => !string.IsNullOrWhiteSpace(m.ModelName))
            .Select(m => (
                Name: m.ModelName.Trim(),
                Price: m.LocalPrice,
                Currency: m.Currency,
                Url: m.SourceUrl,
                Image: m.ImageUrl,
                Availability: (string?)null, // brand catalogues list models, not store stock
                Indicative: true));

    public static (List<ProductModel> Models, List<string> Created) AppendCatalogDiscoveries(
        List<ProductModel> models,
        IEnumerable<(string Name, decimal? Price, string? Currency, string? Url, string? Image, string? Availability, bool Indicative)> entries,
        string? storeName, string? query, string? geo = null, bool fromQueryScopedPage = false)
    {
        var queryTokens = SignificantQueryTokens(query, geo);
        var created = new List<string>();
        if (queryTokens.Count == 0 && !fromQueryScopedPage)
        {
            return (models, created);
        }

        var result = models;
        // Identity keys append the variant NUMBERS explicitly: single digits ("2" vs "3" ton) sit
        // below every token-length floor yet are exactly what keeps sibling SKUs distinct.
        static string IdentityKey(string name) =>
            string.Join(' ', ShortTokens(name)) + "|" + string.Join(',', VariantNumbers(name).OrderBy(n => n, StringComparer.Ordinal));

        var seen = new HashSet<string>(models.Select(m => IdentityKey(m.Name)), StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Name))
            {
                continue;
            }

            var key = IdentityKey(e.Name);
            if (key.StartsWith('|') || !seen.Add(key) || MatchedByAnyModel(e.Name, models))
            {
                continue;
            }

            // The name gate keeps accessories out of DOMAIN-WIDE catalogues — but an entry harvested
            // from the store's OWN search results for this query was already matched by the store's
            // engine, in the store's language. Token-matching English query words against an Arabic
            // product name here rejected correct products ("rice cooker" vs "طنجرة أرز" shares zero
            // tokens; QA extracted the right cooker and this line dropped it). Trust the source page.
            if (!fromQueryScopedPage && !NameMatchesQuery(e.Name, queryTokens))
            {
                continue;
            }

            if (ReferenceEquals(result, models))
            {
                result = new List<ProductModel>(models);
            }

            result.Add(new ProductModel
            {
                Name = e.Name.Trim(),
                ImageUrl = string.IsNullOrWhiteSpace(e.Image) ? null : e.Image,
                Offers = new List<PriceOffer>
                {
                    new()
                    {
                        Source = DomainOf(e.Url) ?? storeName ?? "Store",
                        SourceType = ResultType.StorePage,
                        Price = e.Price,
                        Currency = e.Currency,
                        Url = e.Url,
                        Availability = e.Availability,
                        IsLocal = true,
                        IsIndicative = e.Indicative
                    }
                }
            });
            created.Add(e.Name.Trim());
        }

        return (result, created);
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

        var identity = $"{m.Brand} {m.Model} {m.Name}";
        BrowserPrice? best = null;
        var bestScore = 0;
        var bestHave = 0;
        foreach (var p in pool)
        {
            // A disagreeing variant number (2 vs 3 ton) means a DIFFERENT SKU — its price must never
            // attach to this model. Token overlap alone can't tell siblings apart (Tokens drops the
            // single-digit discriminators), so this veto is what stops cross-attribution.
            if (VariantsDisagree(identity, p.Line))
            {
                continue;
            }

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
        Action<string, string, string, IReadOnlyDictionary<string, object?>?>? record, CancellationToken ct)
    {
        if (observations.Count == 0)
        {
            return;
        }

        try
        {
            await _scrapedPrices.AddRangeAsync(observations, ct);
            record?.Invoke(EventCategory.Profile, "price.persist", "pipeline",
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

    /// <summary>
    /// A price scraped off a rendered store page, with the source line for token matching. <see cref="ImageUrl"/>
    /// is carried only by the persisted-price path (<see cref="AttachScrapedPricesAsync"/>), where the LLM
    /// site-crawl stored a photo on the row; the live browser-harvest sources leave it null (a rendered price
    /// line has no image), which the image-fill below treats as "nothing to attach".
    /// </summary>
    private readonly record struct BrowserPrice(
        decimal Price, string Currency, string Line, string Store, string Url, string? ImageUrl = null);

    private static bool HasPrice(ProductModel m) => m.Offers.Any(o => o.Price is not null);

    private static async Task<IReadOnlyList<CatalogProduct>> SafeCatalog(
        Services.IProviderApi api, string domain, ILogger logger, CancellationToken ct)
    {
        try
        {
            // Metering is the gateway's job now (ambient per-job observer, by construction).
            // UNCAPPED (maxProducts 0 ⇒ vendor ceiling) — the phase budget bounds time, not count.
            return await api.ExtractCatalogAsync(domain, maxProducts: 0, timeoutMs: CatalogTimeoutMs, ct: ct);
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

        var identity = $"{m.Brand} {m.Model} {m.Name}";
        CatalogProduct? best = null;
        var bestScore = 0;
        var bestHave = 0;
        foreach (var c in pool)
        {
            // Variant veto (see BestBrowserMatch): a 3-ton catalogue entry must not price a 2-ton
            // model. The name + SKU carry the discriminator; category is generic, so exclude it here.
            if (VariantsDisagree(identity, $"{c.Name} {c.Sku}"))
            {
                continue;
            }

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
