using System.Security.Cryptography;
using System.Text;
using Daleel.Core.Models;
using Daleel.Core.Persistence;
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
            // Not a machine-readable catalogue (not Shopify / bot-walled). The HTML-crawl mode is the
            // spec's later step — give up visibly rather than silently.
            return new UnitOutcome.Kill($"no machine-readable catalogue on {p.Domain} (html mode not yet supported)");
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

        // Presence must be stamped even for UNCHANGED pages (the watermark advances every sync);
        // entity upserts are skipped for them — that's the cost-∝-change property.
        var geo = ctx.Job.Geo is { Length: > 0 } g ? g : "jordan";
        var currency = Daleel.Core.Geo.GeoProfiles.ResolveOrDefault(geo).Currency;
        var now = DateTimeOffset.UtcNow;
        var storeName = ctx.Job.Query.StartsWith("inventory:") ? p.Domain : ctx.Job.Query;

        var keys = listings.Select(l => ProductProfile.KeyFor(l.Brand, l.Sku, l.Name)).ToList();
        var existing = await db.ScrapedPrices
            .Where(r => r.Provider == "inventory" && r.StoreName == p.Domain && keys.Contains(r.ProductKey))
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
                db.ScrapedPrices.Add(new ScrapedPrice
                {
                    ProductName = l.Name,
                    ProductKey = key,
                    StoreName = p.Domain,
                    Price = l.Price,
                    Currency = currency,
                    SourceUrl = l.Url,
                    ImageUrl = l.ImageUrl,
                    Availability = l.Available ? "in stock" : "out of stock",
                    Provider = "inventory",
                    ScrapedAt = now,
                    LastSeenAt = now
                });
            }
        }

        await db.SaveChangesAsync(ct);

        if (!unchanged)
        {
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
                SearchId = item.SearchJobId.ToString(),
                ProductKey = ProductProfile.KeyFor(l.Brand, l.Sku, l.Name),
                Offers = new[]
                {
                    new EntityOffer
                    {
                        Source = p.Domain,
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
            var saved = await store.SaveAllAsync(docs, ct);
            _logger.LogInformation("Inventory {Domain} p{Page}: {Count} listing(s), {Saved} entity upsert(s).",
                p.Domain, p.Page, listings.Count, saved);
        }

        return UnitOutcome.Ok;
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
