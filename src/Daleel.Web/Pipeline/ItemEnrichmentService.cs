using Daleel.Agent;
using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Search.Providers;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Profiles;
using Daleel.Web.Services;

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

    /// <summary>Min shared significant tokens (brand/model/SKU) to trust a catalogue→item price match.</summary>
    private const int MinMatchTokens = 2;

    private readonly IProductProfileRepository _repo;
    private readonly ProfileOptions _options;
    private readonly IAgentFactory _factory;

    public ItemEnrichmentService(IProductProfileRepository repo, ProfileOptions options, IAgentFactory factory)
    {
        _repo = repo;
        _options = options;
        _factory = factory;
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
        var (pricedModels, priced) = await AttachCatalogPricesAsync(withSpecs, products, progress, Record, ct);

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
    /// For items that still have no price, harvest the top found stores' product catalogues from their
    /// websites (Context.dev's purpose-built <c>/v1/brand/ai/products</c>) and attach a matching priced
    /// offer. Targeted on purpose — we only fill gaps on items already shown, so a store's full catalogue
    /// never floods the results with unrelated products. Best-effort: any failure leaves the item as-is.
    /// </summary>
    private async Task<(List<ProductModel> Models, int Priced)> AttachCatalogPricesAsync(
        List<ProductModel> models, ProductSearchResult products, Action<string> progress,
        Action<string, string, string, IReadOnlyDictionary<string, object?>?> record, CancellationToken ct)
    {
        var key = _factory.Resolve("CONTEXT_DEV_API_KEY");
        if (string.IsNullOrWhiteSpace(key) || !models.Any(m => !HasPrice(m)))
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

        var ctx = new ContextDevProvider(key);
        progress($"Reading {domains.Count} store catalogue(s) for live prices…");

        // Hard cap on the whole catalogue phase so a slow crawl (or a retry on the API's 408) can never
        // blow the background enrichment budget — if it overruns, we just ship the result without it.
        using var catalogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        catalogCts.CancelAfter(TimeSpan.FromMilliseconds(CatalogTimeoutMs + 8_000));

        // Harvest catalogues concurrently (HTTP only, no DbContext) so the slow crawls overlap.
        var catalogues = await Task.WhenAll(domains.Select(async d =>
        {
            var found = await SafeCatalog(ctx, d, catalogCts.Token);
            record(EventCategory.Extract, "catalog.products", "context.dev",
                new Dictionary<string, object?> { ["domain"] = d, ["products"] = found.Count });
            return found;
        }));

        var pool = catalogues.SelectMany(c => c).Where(c => c.Price is not null).ToList();
        if (pool.Count == 0)
        {
            return (models, 0);
        }

        var priced = 0;
        var updated = models.Select(m =>
        {
            if (HasPrice(m) || BestCatalogMatch(m, pool) is not { } match)
            {
                return m;
            }
            priced++;
            var offer = new PriceOffer
            {
                Source = DomainOf(match.Url) ?? "Store",
                SourceType = ResultType.StorePage,
                Price = match.Price,
                Currency = match.Currency,
                Url = match.Url,
                IsLocal = true
            };
            return m with { Offers = m.Offers.Append(offer).ToList() };
        }).ToList();

        if (priced > 0)
        {
            progress($"Filled in live prices for {priced} item(s) from store catalogues.");
        }
        return (updated, priced);
    }

    private static bool HasPrice(ProductModel m) => m.Offers.Any(o => o.Price is not null);

    private static async Task<IReadOnlyList<CatalogProduct>> SafeCatalog(
        ContextDevProvider ctx, string domain, CancellationToken ct)
    {
        try
        {
            return await ctx.ExtractProductsAsync(domain, maxProducts: 12, timeoutMs: CatalogTimeoutMs, cancellationToken: ct);
        }
        catch
        {
            // Any failure (incl. the phase-cap timeout) just means "no catalogue for this store" —
            // it must never fail the enrichment, which has already produced a usable result.
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
        foreach (var c in pool)
        {
            var score = Tokens($"{c.Name} {c.Sku} {c.Category}").Count(want.Contains);
            if (score > bestScore)
            {
                best = c;
                bestScore = score;
            }
        }

        return bestScore >= MinMatchTokens ? best : null;
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
        catch { return null; }
    }

    private async Task SafeUpsert(ProductProfile profile, CancellationToken ct)
    {
        try { await _repo.UpsertAsync(profile, ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort: saving a deep-dive must never fail the search */ }
    }
}
