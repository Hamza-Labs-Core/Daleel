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
    private readonly IScrapedPriceRepository _scrapedPrices;
    private readonly IBrandCatalogService _brandCatalog;
    private readonly ILogger<ItemEnrichmentService> _logger;

    /// <summary>How many of the result's brands get their site catalogue harvested per run (slow crawls).</summary>
    private const int MaxBrandCatalogs = 2;

    public ItemEnrichmentService(
        IProductProfileRepository repo, ProfileOptions options, IAgentFactory factory,
        IScrapedPriceRepository scrapedPrices, IBrandCatalogService brandCatalog,
        ILogger<ItemEnrichmentService> logger)
    {
        _repo = repo;
        _options = options;
        _factory = factory;
        _scrapedPrices = scrapedPrices;
        _brandCatalog = brandCatalog;
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
        var models = products.Models.Take(MaxItems).ToList();
        var enriched = new Dictionary<int, string>();

        // Phase 1 — price comparison + DB-first reuse (sequential: scoped DbContext isn't concurrency-safe).
        var toScrape = new List<(ProductModel m, int idx, string url, string key)>();
        for (var idx = 0; idx < models.Count; idx++)
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

        // Phase 4 — fill PRICES into items that still lack one, from the found stores' catalogues
        // (Context.dev /v1/brand/ai/products). Runs last because the catalogue crawl is the slowest call.
        var (pricedModels, priced) = await AttachCatalogPricesAsync(agent, withSpecs, products, progress, Record, ct);

        // Phase 5 — harvest each surfaced brand's own site into the BrandModel database (specs/prices/images
        // → R2). A pure side effect: it builds the model database for later searches and does not alter the
        // result shown now, so it runs regardless of whether the product enrichment above changed anything.
        await HarvestBrandCatalogsAsync(products, progress, Record, ct);

        if (enriched.Count == 0 && priced == 0)
        {
            return new ItemEnrichmentResult(null, events);
        }

        var rebuilt = pricedModels.Concat(products.Models.Skip(MaxItems)).ToList();

        progress(
            $"Deep-dived {enriched.Count} item(s) — {fresh} new, {enriched.Count - fresh} reused" +
            (priced > 0 ? $"; added live prices to {priced}." : "."));
        return new ItemEnrichmentResult(products with { Models = rebuilt }, events);
    }

    /// <summary>
    /// For items that still have no price, harvest the top found stores' product catalogues and attach a
    /// matching priced offer. Two sources: Context.dev's structured <c>/v1/brand/ai/products</c> first, then
    /// a Cloudflare-Browser-rendered page parsed by <see cref="PriceParser"/> for the stores Context.dev
    /// can't read. Every match is also written to the <see cref="ScrapedPrice"/> history, and a matched
    /// product image is copied into R2. Targeted on purpose — we only fill gaps on items already shown, so a
    /// store's full catalogue never floods the results. Best-effort: any failure leaves the item as-is.
    /// </summary>
    private async Task<(List<ProductModel> Models, int Priced)> AttachCatalogPricesAsync(
        AgentService agent, List<ProductModel> models, ProductSearchResult products, Action<string> progress,
        Action<string, string, string, IReadOnlyDictionary<string, object?>?> record, CancellationToken ct)
    {
        if (!models.Any(m => !HasPrice(m)))
        {
            return (models, 0);
        }

        var domains = products.Stores
            .Select(s => DomainOf(s.Url))
            .Where(d => d is not null).Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCatalogSites)
            .ToList();
        if (domains.Count == 0)
        {
            return (models, 0);
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

            pool = catalogues.SelectMany(c => c.found).Where(c => c.Price is not null).ToList();
            // Only the domains Context.dev returned nothing priced for fall through to the browser pass.
            browserUnpriced = catalogues.Where(c => c.found.All(p => p.Price is null))
                .Select(c => c.domain).ToList();
        }

        var browserPrices = await HarvestViaBrowserAsync(agent, browserUnpriced, products.Query, record, catalogCts.Token);

        if (pool.Count == 0 && browserPrices.Count == 0)
        {
            return (models, 0);
        }

        var now = _options.Now();
        var observations = new List<ScrapedPrice>();
        var priced = 0;
        var updated = new List<ProductModel>(models.Count);
        foreach (var m in models)
        {
            if (HasPrice(m))
            {
                updated.Add(m);
                continue;
            }

            // Prefer the structured Context.dev match; fall back to a browser-scraped line price.
            var match = BestCatalogMatch(m, pool);
            var offer = match is { } c
                ? new PriceOffer
                {
                    Source = DomainOf(c.Url) ?? "Store", SourceType = ResultType.StorePage,
                    Price = c.Price, Currency = c.Currency, Url = c.Url, IsLocal = true
                }
                : BestBrowserMatch(m, browserPrices) is { } bp
                    ? new PriceOffer
                    {
                        Source = bp.Store, SourceType = ResultType.StorePage,
                        Price = bp.Price, Currency = bp.Currency, Url = bp.Url, IsLocal = true
                    }
                    : null;

            if (offer is null)
            {
                updated.Add(m);
                continue;
            }

            priced++;
            observations.Add(new ScrapedPrice
            {
                ProductName = m.Name,
                ProductKey = ProductProfile.KeyFor(m.Brand, m.Model, m.Name),
                StoreName = offer.Source,
                Price = offer.Price,
                Currency = offer.Currency,
                SourceUrl = offer.Url,
                Provider = match is not null ? "context.dev" : "cloudflare-browser",
                ScrapedAt = now
            });

            var withOffer = m with { Offers = m.Offers.Append(offer).ToList() };

            // The catalogue entry usually carries a product image — fill it in when the model has none,
            // keeping the original source URL (the UI renders external image URLs directly).
            if (string.IsNullOrWhiteSpace(withOffer.ImageUrl) && match is { ImageUrl: { Length: > 0 } img })
            {
                withOffer = withOffer with { ImageUrl = img };
            }

            updated.Add(withOffer);
        }

        // Persist the observations as a timestamped batch — the price history the comparison view reads.
        await PersistObservations(observations, record, ct);

        if (priced > 0)
        {
            progress($"Filled in live prices for {priced} item(s) from store catalogues.");
        }
        return (updated, priced);
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
            return await ctx.ExtractProductsAsync(domain, maxProducts: 12, timeoutMs: CatalogTimeoutMs, cancellationToken: ct);
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
