using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Daleel.Core.Models;
using Daleel.Core.Persistence;
using Daleel.Search.Abstractions;
using Daleel.Search.Http;
using Daleel.Web.Data;
using Daleel.Web.Persistence;
using Daleel.Web.Pipeline.Enrichment;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daleel.Web.Pipeline.Inventory;

/// <summary>
/// inventory.sync — per monitored store: probes the catalogue interface (Shopify /products.json
/// first; the crawl-based mode for HTML stores comes later) and fans out one inventory.page unit
/// per catalogue page plus the settle-gated finalize. The queue is the recursion mechanism: this
/// unit does no fetching beyond the probe.
/// </summary>
public sealed class InventorySyncHandler : IEnrichmentUnitHandler
{
    /// <summary>Loop-safety ceiling on pagination (NOT a result cap — Shopify pages hold 250 items,
    /// so this admits 50k+ products; a store larger than that indicates a paging loop, not inventory).</summary>
    internal const int MaxPages = 200;

    private readonly ILogger<InventorySyncHandler> _logger;

    public InventorySyncHandler(ILogger<InventorySyncHandler> logger) => _logger = logger;

    public string Kind => EnrichmentUnit.InventorySync;

    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        if (EnrichmentWorkQueue.ReadPayload<InventorySyncPayload>(item.Payload) is not { } p)
        {
            return new UnitOutcome.Kill("missing sync payload");
        }

        var catalog = ctx.Services.GetRequiredService<IStoreCatalogClient>();
        var probe = await catalog.GetPageAsync(p.Domain, page: 1, ct);
        if (probe is null)
        {
            // Not a machine-readable catalogue (neither Shopify nor Woo) — fall to the HTML mode:
            // discover the store's LISTING pages (sitemap first, homepage nav assess as the one LLM
            // fallback) and fan out one inventory.htmlpage unit per listing page.
            return await FanOutHtmlModeAsync(item, ctx, p, ct);
        }

        // Fan out one unit per page up to the ceiling; page units self-terminate on the first empty
        // page, so a small store costs exactly its page count. EnqueueFanOutAsync makes a re-leased
        // sync crash-safe (children are not duplicated).
        var children = new List<EnrichmentWorkItem>();
        for (var page = 1; page <= MaxPages; page++)
        {
            children.Add(HandlerHelpers.Child(item, EnrichmentUnit.InventoryPage,
                EnrichmentWorkQueue.Payload(new InventoryPagePayload(p.StoreId, p.Domain, page, p.SyncStartedAt))));
        }

        children.Add(HandlerHelpers.Child(item, EnrichmentUnit.InventoryFinalize,
            EnrichmentWorkQueue.Payload(p), notBefore: TimeSpan.FromSeconds(30)));
        await ctx.Queue.EnqueueFanOutAsync(item.SearchJobId, Kind, children, ct);

        _logger.LogInformation("Inventory sync {Domain}: fanned out (Shopify catalogue detected).", p.Domain);
        return UnitOutcome.Ok;
    }

    /// <summary>
    /// The HTML-store path: one <see cref="EnrichmentUnit.InventoryHtmlPage"/> unit per discovered
    /// LISTING page (a category page carries many products — the fan-out is never per product),
    /// UNCAPPED per the no-result-caps invariant, plus the settle-gated finalize. A store with no
    /// discoverable catalogue at all dies visibly, never silently.
    /// </summary>
    private async Task<UnitOutcome> FanOutHtmlModeAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, InventorySyncPayload p, CancellationToken ct)
    {
        var discovery = ctx.Services.GetRequiredService<IHtmlCatalogDiscovery>();

        Daleel.Agent.AgentService? agent = null;
        try
        {
            agent = ctx.Agent();
        }
        catch
        {
            // no LLM configured — sitemap-only discovery still works
        }

        IReadOnlyList<string> listingPages;
        try
        {
            listingPages = await discovery.DiscoverListingPagesAsync(p.Domain, agent, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new UnitOutcome.Retry($"catalogue discovery failed on {p.Domain}: {ex.Message}");
        }

        if (listingPages.Count == 0)
        {
            return new UnitOutcome.Kill(
                $"no discoverable catalogue on {p.Domain} (no Shopify/Woo JSON, no sitemap categories, no nav listing pages)");
        }

        var children = listingPages
            .Select(url => HandlerHelpers.Child(item, EnrichmentUnit.InventoryHtmlPage,
                EnrichmentWorkQueue.Payload(new InventoryHtmlPagePayload(p.StoreId, p.Domain, url, p.SyncStartedAt))))
            .ToList();
        children.Add(HandlerHelpers.Child(item, EnrichmentUnit.InventoryFinalize,
            EnrichmentWorkQueue.Payload(p), notBefore: TimeSpan.FromSeconds(30)));
        await ctx.Queue.EnqueueFanOutAsync(item.SearchJobId, Kind, children, ct);

        _logger.LogInformation(
            "Inventory sync {Domain}: HTML mode — fanned out {Count} listing page(s).", p.Domain, listingPages.Count);
        return UnitOutcome.Ok;
    }
}

/// <summary>
/// inventory.page — one catalogue page: fetch, hash-skip when unchanged since the last sync, else
/// upsert every listing as an entity (identity-keyed, so re-syncs converge onto the same items and
/// dedup keeps one item with this store's offer on it) plus a ScrapedPrice presence/price row.
/// </summary>
public sealed class InventoryPageHandler : IEnrichmentUnitHandler
{
    private readonly ILogger<InventoryPageHandler> _logger;

    public InventoryPageHandler(ILogger<InventoryPageHandler> logger) => _logger = logger;

    public string Kind => EnrichmentUnit.InventoryPage;

    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        if (EnrichmentWorkQueue.ReadPayload<InventoryPagePayload>(item.Payload) is not { } p)
        {
            return new UnitOutcome.Kill("missing page payload");
        }

        var catalog = ctx.Services.GetRequiredService<IStoreCatalogClient>();
        var result = await catalog.GetPageAsync(p.Domain, p.Page, ct);
        if (result is null)
        {
            return new UnitOutcome.Retry($"catalogue page {p.Page} unreadable");
        }

        var (listings, raw) = result.Value;
        if (listings.Count == 0)
        {
            return UnitOutcome.Ok; // past the end of the catalogue — nothing to do
        }

        var db = ctx.Services.GetRequiredService<DaleelDbContext>();
        var url = $"https://{p.Domain}/products.json?page={p.Page}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        var pageRow = await db.StoreCatalogPages.FirstOrDefaultAsync(x => x.Domain == p.Domain && x.Url == url, ct);
        var unchanged = pageRow is not null && pageRow.ContentHash == hash;
        if (pageRow is null)
        {
            db.StoreCatalogPages.Add(new StoreCatalogPage
            {
                Domain = p.Domain, Url = url, ContentHash = hash, LastSeenAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            pageRow.ContentHash = hash;
            pageRow.LastSeenAt = DateTimeOffset.UtcNow;
        }

        var saved = await InventoryUpsert.ApplyAsync(
            db, ctx, item.SearchJobId, p.Domain, listings, pageChanged: !unchanged, DateTimeOffset.UtcNow, ct);
        if (!unchanged)
        {
            _logger.LogInformation("Inventory {Domain} p{Page}: {Count} listing(s), {Saved} entity upsert(s).",
                p.Domain, p.Page, listings.Count, saved);
        }

        return UnitOutcome.Ok;
    }
}

/// <summary>
/// The shared upsert core of the inventory page units (JSON and HTML modes alike): presence is
/// stamped for EVERY listing (the watermark advances every sync — that's what the finalize pass's
/// delisting flip keys on), while identity-keyed entity upserts run only when the page CHANGED —
/// the cost-∝-change property. Saves via the caller's DbContext, so page-row mutations staged by the
/// caller commit atomically with the presence rows.
/// </summary>
internal static class InventoryUpsert
{
    /// <summary>Stamps/creates the <see cref="ScrapedPrice"/> presence rows and (when
    /// <paramref name="pageChanged"/>) upserts one entity+offer per listing. Returns entities saved.</summary>
    public static async Task<int> ApplyAsync(
        DaleelDbContext db, EnrichmentUnitContext ctx, int searchJobId, string domain,
        IReadOnlyList<InventoryListing> listings, bool pageChanged, DateTimeOffset now, CancellationToken ct)
    {
        var geo = ctx.Job.Geo is { Length: > 0 } g ? g : "jordan";
        var currency = Daleel.Core.Geo.GeoProfiles.ResolveOrDefault(geo).Currency;

        var keys = listings.Select(l => ProductProfile.KeyFor(l.Brand, l.Sku, l.Name)).ToList();
        var existing = keys.Count == 0
            ? new List<ScrapedPrice>()
            : await db.ScrapedPrices
                .Where(r => r.Provider == "inventory" && r.StoreName == domain && keys.Contains(r.ProductKey))
                .ToListAsync(ct);
        var byKey = existing.GroupBy(r => r.ProductKey).ToDictionary(x => x.Key, x => x.First());

        foreach (var (l, key) in listings.Zip(keys))
        {
            if (byKey.TryGetValue(key, out var row))
            {
                row.LastSeenAt = now;
                row.Price = l.Price;
                row.Availability = l.Available ? "in stock" : "out of stock";
                row.ImageUrl = l.ImageUrl ?? row.ImageUrl;
                row.ScrapedAt = now;
            }
            else
            {
                var added = new ScrapedPrice
                {
                    ProductName = l.Name,
                    ProductKey = key,
                    StoreName = domain,
                    Price = l.Price,
                    Currency = currency,
                    SourceUrl = l.Url,
                    ImageUrl = l.ImageUrl,
                    Availability = l.Available ? "in stock" : "out of stock",
                    Provider = "inventory",
                    ScrapedAt = now,
                    LastSeenAt = now
                };
                db.ScrapedPrices.Add(added);
                byKey[key] = added; // a key repeated within one page updates, never duplicates
            }
        }

        await db.SaveChangesAsync(ct);

        if (!pageChanged || listings.Count == 0)
        {
            return 0;
        }

        var store = ctx.Services.GetRequiredService<ISearchEntityStore>();
        var docs = listings.Select(l => new EntityDocument
        {
            Id = StableId.ForEntity(SearchIntentType.Product, l.Brand, l.Sku, l.Name),
            Intent = SearchIntentType.Product,
            Name = l.Name,
            Brand = l.Brand,
            Model = l.Sku,
            Category = l.Category,
            Geo = geo,
            ImageUrl = l.ImageUrl,
            SearchId = searchJobId.ToString(),
            ProductKey = ProductProfile.KeyFor(l.Brand, l.Sku, l.Name),
            Offers = new[]
            {
                new EntityOffer
                {
                    Source = domain,
                    Price = l.Price,
                    Currency = currency,
                    Url = l.Url,
                    Condition = null,
                    ImageUrls = l.ImageUrl is { Length: > 0 } img ? new[] { img } : Array.Empty<string>(),
                    ListingName = l.Name
                }
            },
            CapturedAt = now
        });
        return await store.SaveAllAsync(docs, ct);
    }
}

/// <summary>
/// inventory.htmlpage — one LISTING/category page of an HTML store: fetch through the full provider
/// chain, hash-skip when unchanged (presence stamped from the page's remembered product keys — zero
/// LLM), else LLM-extract every card (markdown-first, strip+chunk — never truncate) and upsert
/// through the SAME core as the JSON mode. The unit then follows the page's own next-page links up
/// to a loop-safety ceiling, deduping against its own walk and against pages another unit already
/// covered this sync. Best-effort throughout: a mid-walk failure keeps everything already landed.
/// </summary>
public sealed class InventoryHtmlPageHandler : IEnrichmentUnitHandler
{
    private readonly ILogger<InventoryHtmlPageHandler> _logger;

    public InventoryHtmlPageHandler(ILogger<InventoryHtmlPageHandler> logger) => _logger = logger;

    public string Kind => EnrichmentUnit.InventoryHtmlPage;

    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        if (EnrichmentWorkQueue.ReadPayload<InventoryHtmlPagePayload>(item.Payload) is not { } p ||
            string.IsNullOrWhiteSpace(p.Url))
        {
            return new UnitOutcome.Kill("missing html-page payload");
        }

        var providers = ctx.Services.GetRequiredService<Daleel.Web.Services.IProviderApi>();
        var db = ctx.Services.GetRequiredService<DaleelDbContext>();
        var geo = Daleel.Core.Geo.GeoProfiles.ResolveOrDefault(ctx.Job.Geo is { Length: > 0 } g ? g : "jordan");

        // Pagination loop-safety ceiling (NOT a result cap — a listing deeper than this indicates a
        // paging loop; every page walked lands its products regardless of where the walk stops).
        var ceiling = PipelineLimits.InventoryHtmlMaxPages;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var url = p.Url;
        var pages = 0;
        var extractedPages = 0;
        var totalListings = 0;

        while (url is not null && pages < ceiling && !ct.IsCancellationRequested)
        {
            if (!visited.Add(url) ||
                !HtmlCatalogDiscovery.SameDomain(url, p.Domain) ||
                !SsrfGuard.IsSafePublicUrl(url))
            {
                break; // walked already / off-site or unsafe next-link — stop, keep what landed
            }

            var row = await db.StoreCatalogPages.FirstOrDefaultAsync(
                x => x.Domain == p.Domain && x.Url == url, ct);
            if (pages > 0 && row is not null && row.LastSeenAt >= p.SyncStartedAt)
            {
                break; // another listing's walk already covered this page THIS sync
            }

            ScrapedPage? page = null;
            try
            {
                // The FULL provider fallback chain (Context.dev → CF Browser) — the every-fetch-path
                // invariant; markdown-first for the extractor.
                page = await providers.ScrapePageAsync(url, ScrapeFormat.Markdown, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // handled below as an empty read
            }

            if (page?.Content is not { Length: > 0 } content)
            {
                if (pages == 0)
                {
                    return new UnitOutcome.Retry($"listing page unreadable: {url}");
                }

                break; // partial walk — best-effort keeps every page already landed
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            string? next;
            if (row is not null && row.ContentHash == hash)
            {
                // Hash-skip: zero LLM. The page's products are exactly the remembered set — stamp
                // their presence watermark — and the walk continues on the remembered next link.
                row.LastSeenAt = now;
                var keys = ReadKeys(row.ProductKeysJson);
                if (keys.Count > 0)
                {
                    var presence = await db.ScrapedPrices
                        .Where(r => r.Provider == "inventory" && r.StoreName == p.Domain && keys.Contains(r.ProductKey))
                        .ToListAsync(ct);
                    foreach (var r in presence)
                    {
                        r.LastSeenAt = now;
                    }
                }

                await db.SaveChangesAsync(ct);
                next = row.NextUrl;
            }
            else
            {
                var result = await ctx.Agent().ExtractStoreCatalogPageAsync(content, url, geo, ct);
                var listings = MapListings(result.Products);
                next = result.NextPageUrl is { } n && HtmlCatalogDiscovery.SameDomain(n, p.Domain) ? n : null;

                if (row is null)
                {
                    row = new StoreCatalogPage { Domain = p.Domain, Url = url };
                    db.StoreCatalogPages.Add(row);
                }

                // Never latch an empty read: the hash+keys pair is only advanced when extraction
                // yielded products, so a page read as empty (LLM hiccup or genuinely bare) re-extracts
                // next sync instead of being hash-skipped into permanent emptiness.
                if (listings.Count > 0)
                {
                    row.ContentHash = hash;
                    row.ProductKeysJson = JsonSerializer.Serialize(
                        listings.Select(l => ProductProfile.KeyFor(l.Brand, l.Sku, l.Name)).Distinct().ToList());
                }

                row.LastSeenAt = now;
                row.NextUrl = next;

                var saved = await InventoryUpsert.ApplyAsync(
                    db, ctx, item.SearchJobId, p.Domain, listings, pageChanged: true, now, ct);
                extractedPages++;
                totalListings += listings.Count;
                _logger.LogInformation(
                    "Inventory {Domain} html page {Url}: {Count} listing(s), {Saved} entity upsert(s).",
                    p.Domain, url, listings.Count, saved);
            }

            pages++;
            url = next;
        }

        _logger.LogInformation(
            "Inventory {Domain} listing walk {Entry}: {Pages} page(s) ({Extracted} extracted, {Skipped} hash-skipped), {Total} listing(s).",
            p.Domain, p.Url, pages, extractedPages, pages - extractedPages, totalListings);
        return UnitOutcome.Ok;
    }

    /// <summary>Maps crawl listings onto the inventory record (deduped by product key). The crawler's
    /// MODEL slot carries the identity the search pipeline keys on, so it wins over the variant SKU.</summary>
    internal static IReadOnlyList<InventoryListing> MapListings(IReadOnlyList<ProductListing> products)
    {
        var list = new List<InventoryListing>(products.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in products)
        {
            if (string.IsNullOrWhiteSpace(product.Name))
            {
                continue;
            }

            var l = ToInventoryListing(product);
            if (seen.Add(ProductProfile.KeyFor(l.Brand, l.Sku, l.Name)))
            {
                list.Add(l);
            }
        }

        return list;
    }

    /// <summary>Availability defaults to available — a card that doesn't say "out of stock" is buyable
    /// (mirroring how the JSON modes read a present listing).</summary>
    internal static InventoryListing ToInventoryListing(ProductListing p) => new(
        Name: p.Name.Trim(),
        Brand: Clean(p.Brand),
        Sku: Clean(p.Model) ?? Clean(p.Sku),
        Category: null,
        Price: p.Price,
        Available: p.Availability is not { Length: > 0 } a ||
                   !a.Contains("out of stock", StringComparison.OrdinalIgnoreCase),
        Url: Clean(p.Url),
        ImageUrl: Clean(p.ImageUrl));

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static IReadOnlyList<string> ReadKeys(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>
/// inventory.finalize — settle-gated (same OpenCount gate as Synthesize): items of this store NOT
/// seen this sync get their store offer flipped to unavailable (never deleted), and the store row
/// is stamped with the sync time + live item count.
/// </summary>
public sealed class InventoryFinalizeHandler : IEnrichmentUnitHandler
{
    private static readonly TimeSpan SettleRetryDelay = TimeSpan.FromSeconds(20);

    private readonly ILogger<InventoryFinalizeHandler> _logger;

    public InventoryFinalizeHandler(ILogger<InventoryFinalizeHandler> logger) => _logger = logger;

    public string Kind => EnrichmentUnit.InventoryFinalize;

    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        if (EnrichmentWorkQueue.ReadPayload<InventorySyncPayload>(item.Payload) is not { } p)
        {
            return new UnitOutcome.Kill("missing sync payload");
        }

        var open = await ctx.Queue.OpenCountAsync(item.SearchJobId, ct);
        var lastChance = item.Attempts >= item.MaxAttempts - 1;
        if (open > 1 && !lastChance)
        {
            return new UnitOutcome.Retry("pages still syncing", SettleRetryDelay);
        }

        var db = ctx.Services.GetRequiredService<DaleelDbContext>();

        // Presence: this store's inventory rows whose watermark predates this sync were NOT on the
        // store this run — flip their availability (row + the item's offer), never delete.
        var missing = await db.ScrapedPrices
            .Where(r => r.Provider == "inventory" && r.StoreName == p.Domain &&
                        (r.LastSeenAt == null || r.LastSeenAt < p.SyncStartedAt) &&
                        r.Availability != "unavailable")
            .ToListAsync(ct);
        var entities = ctx.Services.GetRequiredService<ISearchEntityStore>();
        foreach (var row in missing)
        {
            row.Availability = "unavailable";
            await FlipOfferAsync(db, entities, row, p.Domain, ct);
        }

        var live = await db.ScrapedPrices.CountAsync(
            r => r.Provider == "inventory" && r.StoreName == p.Domain && r.LastSeenAt >= p.SyncStartedAt, ct);

        var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == p.StoreId, ct);
        if (store is not null)
        {
            store.LastInventorySyncAt = DateTimeOffset.UtcNow;
            store.LastInventoryCount = live;
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Inventory sync {Domain} finalized: {Live} live item(s), {Missing} delisting flip(s).",
            p.Domain, live, missing.Count);
        return UnitOutcome.Ok;
    }

    /// <summary>Best-effort: flip this store's offer to unavailable on the item's entity document.</summary>
    private static async Task FlipOfferAsync(
        DaleelDbContext db, ISearchEntityStore entities, ScrapedPrice row, string domain, CancellationToken ct)
    {
        try
        {
            var record = await db.EntityRecords.AsNoTracking()
                .Where(r => r.ProductKey == row.ProductKey && r.MergedIntoId == null)
                .FirstOrDefaultAsync(ct);
            if (record is null ||
                await entities.GetAsync(record.Id, SearchIntentType.Product, ct) is not { } doc)
            {
                return;
            }

            var changed = false;
            var offers = doc.Offers.Select(o =>
            {
                if (string.Equals(o.Source, domain, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(o.Availability, "unavailable", StringComparison.OrdinalIgnoreCase))
                {
                    changed = true;
                    return o with { Availability = "unavailable" };
                }

                return o;
            }).ToList();
            if (changed)
            {
                await entities.SaveAsync(doc with { Offers = offers }, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // presence flip is best-effort — the ScrapedPrice row already carries the truth
        }
    }
}
