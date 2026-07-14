using System.Text.Json;
using Daleel.Core.Geo;
using Daleel.Core.Models;
using Daleel.Core.Persistence;
using Daleel.Pipeline.Extraction;
using Daleel.Web.Data;
using Daleel.Web.Persistence;
using Daleel.Web.Profiles;
using Daleel.Web.Services;
using Daleel.Web.Storage;
using Elsa.Extensions;
using Elsa.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// Turns a store's <see cref="StoreAssessment"/> into the single concrete listing URL the store crawler
/// visits first. Pure and static so the navigation choice — which entry point wins, how the query is injected
/// into a search template — is unit-testable without a workflow or an LLM.
/// </summary>
public static class CrawlNavigation
{
    /// <summary>
    /// Resolves the first listing URL to crawl from a store assessment, honouring the LLM's recommended
    /// approach and falling back through the other entry points (search → category → api → sitemap) when the
    /// recommended one isn't available. Returns null when the store exposes no reachable catalogue.
    /// </summary>
    public static string? ResolveEntryPoint(StoreAssessment assessment, string query)
    {
        var chosen = assessment.RecommendedApproach switch
        {
            CrawlApproach.Search => BuildSearchUrl(assessment.SearchUrlTemplate, query),
            CrawlApproach.Category => assessment.ListingUrls.FirstOrDefault(),
            CrawlApproach.Api => assessment.ApiEndpoints.FirstOrDefault(),
            CrawlApproach.Sitemap => assessment.SitemapUrl,
            _ => null
        };
        if (chosen is { Length: > 0 })
        {
            return chosen;
        }

        // Fall back through the remaining entry points in order of usefulness for finding matches: the
        // store's own search (most targeted), then a category page, then a product API, then the sitemap.
        return BuildSearchUrl(assessment.SearchUrlTemplate, query)
            ?? assessment.ListingUrls.FirstOrDefault()
            ?? assessment.ApiEndpoints.FirstOrDefault()
            ?? assessment.SitemapUrl;
    }

    /// <summary>Substitutes the URL-encoded query into a <c>{query}</c> search template, or null when there's no template.</summary>
    public static string? BuildSearchUrl(string? searchTemplate, string query)
    {
        if (string.IsNullOrWhiteSpace(searchTemplate) || !searchTemplate.Contains("{query}", StringComparison.Ordinal))
        {
            return null;
        }

        return searchTemplate.Replace("{query}", Uri.EscapeDataString(query.Trim()), StringComparison.Ordinal);
    }
}

/// <summary>
/// Shared availability gate + child-dispatch for the specialised site crawlers (store / brand). Centralises
/// the check — a crawl runs only when Cloudflare Browser Rendering is configured (so JS-heavy sites render)
/// and the runtime kill-switch is on — so both research sub-workflows can't drift.
/// </summary>
internal static class CrawlDispatch
{
    /// <summary>Runtime kill-switch for the LLM site crawlers (defaults on when CF Browser is configured).</summary>
    public const string EnabledFlag = "crawl.enabled";

    /// <summary>
    /// Runs the crawl <typeparamref name="TWorkflow"/> for a site in its own DI scope, seeded from the parent
    /// run (the shared agent + progress sink are wired automatically; <paramref name="seed"/> fills the rest),
    /// returning the finished state — or null when the crawl is unavailable (no CF Browser, or the switch is
    /// off) so the caller can fall back. Bounded by the store timeout + the outer deadline; best-effort.
    /// </summary>
    public static async Task<TState?> TryRunAsync<TWorkflow, TState>(
        ActivityExecutionContext context, Action<TState, SubWorkflowServices> seed, CancellationToken ct)
        where TWorkflow : IWorkflow, new()
        where TState : SubWorkflowState
    {
        if (!await IsAvailableAsync(context, ct))
        {
            return null;
        }

        var services = context.GetRequiredService<SubWorkflowServices>();
        var scopeFactory = context.GetRequiredService<IServiceScopeFactory>();
        return await SubWorkflowDispatcher.DispatchAsync<TWorkflow, TState>(
            scopeFactory,
            (s, svc) =>
            {
                svc.Agent = services.Agent;
                svc.Progress = services.Progress;
                seed(s, svc);
            },
            SubWorkflowDispatcher.StoreResearchTimeout, ct);
    }

    /// <summary>
    /// True when the crawlers' renderer (Cloudflare Browser Rendering) is configured — both CF keys resolvable,
    /// mirroring the composition-root gate (<c>AgentFactory</c>) — AND the runtime flag is on. Best-effort: a
    /// config-read failure degrades to "not disabled" (the CF keys already gate it).
    /// </summary>
    private static async Task<bool> IsAvailableAsync(ActivityExecutionContext context, CancellationToken ct)
    {
        if (context.GetService<IAgentFactory>() is not { } factory ||
            factory.Resolve("CLOUDFLARE_ACCOUNT_ID") is null ||
            factory.Resolve("CLOUDFLARE_API_TOKEN") is null)
        {
            return false;
        }

        if (context.GetService<ISystemConfigService>() is not { } config)
        {
            return true; // no config service (test harness) — availability rests on the CF keys being present
        }

        try
        {
            return await config.GetBoolAsync(EnabledFlag, true, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return true;
        }
    }
}

/// <summary>
/// Shared best-effort plumbing for the crawl activities: render a page through the metered scraper and archive
/// it to R2 (save-everything), and accumulate discovered products without duplicates. R2 writes and render
/// failures never fault the crawl.
/// </summary>
internal static class CrawlPipeline
{
    /// <summary>
    /// Renders <paramref name="url"/> through the metered scraper (Context.dev → Cloudflare Browser Rendering,
    /// SSRF-guarded inside <c>ReadPageAsync</c>) and, when it yields content, archives the page to R2 before
    /// returning it. Returns null when nothing renders.
    /// </summary>
    public static async Task<ScrapedPageLike?> RenderAndSaveAsync(
        ActivityExecutionContext context, string siteUrl, string? searchId, string url, CancellationToken ct)
    {
        var services = context.GetRequiredService<SubWorkflowServices>();
        var page = await services.Agent.ReadPageAsync(url, ct);
        if (page is null || string.IsNullOrWhiteSpace(page.Content))
        {
            return null;
        }

        await SaveCrawlPageAsync(context, siteUrl, searchId, url, page.Provider, page.Content, ct);
        return new ScrapedPageLike(page.Content, string.IsNullOrWhiteSpace(page.Provider) ? "scraper" : page.Provider);
    }

    /// <summary>
    /// Adds <paramref name="products"/> to <paramref name="discovered"/>, skipping ones already present (same
    /// detail URL, else same brand+model+name). Returns how many were newly added.
    /// </summary>
    public static int AddDiscovered(List<ProductListing> discovered, IReadOnlyList<ProductListing> products)
    {
        if (products.Count == 0)
        {
            return 0;
        }

        var seen = new HashSet<string>(discovered.Select(DedupKey), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var p in products)
        {
            if (!string.IsNullOrWhiteSpace(p.Name) && seen.Add(DedupKey(p)))
            {
                discovered.Add(p);
                added++;
            }
        }

        return added;
    }

    /// <summary>Identity for dedup: the detail URL when present (canonical), else brand+model+name.</summary>
    private static string DedupKey(ProductListing p) =>
        !string.IsNullOrWhiteSpace(p.Url)
            ? p.Url!.Trim().ToLowerInvariant()
            : string.Join('|', new[] { p.Brand, p.Model, p.Name }
                .Select(s => (s ?? string.Empty).Trim().ToLowerInvariant()));

    /// <summary>
    /// Archives a fetched crawl page to R2 under <c>crawl/{yyyyMMdd}/{host}/{slug}.json</c> in the Logs bucket
    /// — the same save-everything envelope the harvest pages use. Guarded and swallowing.
    /// </summary>
    private static async Task SaveCrawlPageAsync(
        ActivityExecutionContext context, string siteUrl, string? searchId, string url, string provider, string content, CancellationToken ct)
    {
        if (context.GetService<IR2StorageService>() is not { } r2)
        {
            return;
        }

        try
        {
            var now = context.GetService<ProfileOptions>()?.Now() ?? DateTimeOffset.UtcNow;
            var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "unknown";
            var key = $"crawl/{now:yyyyMMdd}/{host}/{Slug(url)}.json";
            var envelope = JsonSerializer.Serialize(new
            {
                url, fetchedAt = now, provider, searchId, site = siteUrl, chars = content.Length, content
            });
            await r2.StoreJsonAsync(envelope, key, R2Bucket.Logs, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // save-everything is best-effort — never fault the crawl over an archive write.
        }
    }

    /// <summary>Last 40 URL-safe characters of the URL, so the R2 key is stable and filesystem-safe.</summary>
    private static string Slug(string url)
    {
        var cleaned = new string(url.Where(char.IsLetterOrDigit).ToArray());
        if (cleaned.Length == 0)
        {
            return "page";
        }

        return cleaned.Length <= 40 ? cleaned : cleaned[^40..];
    }
}

/// <summary>
/// Shared persistence for the crawlers: writes discovered products to R2 (self-contained EntityDocuments,
/// the source of truth) + the Postgres index, and priced ones to the ScrapedPrice series. Two entry points —
/// a batch of listing-level products (store/brand crawl remainder) and one rich product-detail record (the
/// ProductDetailWorkflow). All best-effort: an R2/DB failure is logged and never faults the crawl.
/// </summary>
internal static class CrawlPersistence
{
    /// <summary>Batch-persists listing-level products (deduped into models) + their prices. Returns (entities, prices).</summary>
    public static async Task<(int Entities, int Prices)> PersistListingsAsync(
        ActivityExecutionContext context, IReadOnlyList<ProductListing> products,
        GeoProfile geo, string query, string siteName, string? searchId, ILogger logger, CancellationToken ct)
    {
        if (products.Count == 0)
        {
            return (0, 0);
        }

        var now = Now(context);
        var models = ListingAggregator.Aggregate(products);
        var result = new ProductSearchResult { Query = query, Geo = geo.Key, Country = geo.Country, Models = models };
        var docs = EntityDocumentMapper.ToDocuments(result, SearchIntentType.Product, searchId, now);

        var entities = 0;
        if (docs.Count > 0 && context.GetService<ISearchEntityStore>() is { } store)
        {
            try { entities = await store.SaveAllAsync(docs, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to persist {Count} crawled entities", docs.Count); }
        }

        var prices = await PersistPricesAsync(context, products, siteName, now, logger, ct);
        return (entities, prices);
    }

    /// <summary>
    /// Persists ONE product's full detail as a rich EntityDocument — preserving ALL images and the extra
    /// schema-less fields (features/related/reviews folded into Specs by the extractor) that the listing-level
    /// mapper would drop — plus its price observation. Returns true when the entity was written.
    /// </summary>
    public static async Task<bool> PersistDetailAsync(
        ActivityExecutionContext context, ProductListing listing, ProductDetail? detail,
        GeoProfile geo, string query, string siteName, string? searchId, ILogger logger, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(listing.Name) ? (listing.Model ?? string.Empty) : listing.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var now = Now(context);
        var images = detail?.Images is { Count: > 0 } imgs
            ? imgs
            : listing.ImageUrl is { Length: > 0 } one ? new[] { one } : Array.Empty<string>();

        var summary = detail?.Reviews is { Count: > 0 } reviews
            ? string.Join(" | ", reviews.Take(3).Select(r => r.Text))
            : null;

        var doc = new EntityDocument
        {
            Id = StableId.ForEntity(SearchIntentType.Product, listing.Brand, listing.Model, name),
            Intent = SearchIntentType.Product,
            Name = name,
            Brand = listing.Brand,
            Model = listing.Model,
            ImageUrl = images.FirstOrDefault(),
            ImageUrls = images,
            Geo = geo.Key,
            Country = geo.Country,
            Query = query,
            SearchId = searchId,
            BrandId = string.IsNullOrWhiteSpace(listing.Brand) ? null : StableId.ForBrand(listing.Brand),
            ProductKey = ProductProfile.KeyFor(listing.Brand, listing.Model, name),
            Specs = listing.Specs.Count == 0
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(listing.Specs, StringComparer.OrdinalIgnoreCase),
            Offers = new[]
            {
                new EntityOffer
                {
                    Source = siteName,
                    Price = listing.Price ?? detail?.Price,
                    Currency = listing.Currency ?? detail?.Currency,
                    Url = listing.Url,
                    Condition = listing.Condition
                }
            },
            Summary = summary,
            CapturedAt = now
        };

        var wrote = false;
        if (context.GetService<ISearchEntityStore>() is { } store)
        {
            try { wrote = await store.SaveAsync(doc, ct) is not null; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to persist crawled product '{Name}'", name); }
        }

        await PersistPricesAsync(context, new[] { listing }, siteName, now, logger, ct);
        return wrote;
    }

    /// <summary>Writes one ScrapedPrice row per priced product, keyed by the shared normalized product key.</summary>
    private static async Task<int> PersistPricesAsync(
        ActivityExecutionContext context, IReadOnlyList<ProductListing> products,
        string siteName, DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        var rows = products
            .Where(p => p.Price is not null && !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new ScrapedPrice
            {
                ProductName = p.Name,
                ProductKey = ProductProfile.KeyFor(p.Brand, p.Model, p.Name),
                StoreName = siteName,
                Price = p.Price,
                Currency = p.Currency,
                SourceUrl = p.Url,
                Provider = "site-crawl",
                ScrapedAt = now
            })
            .ToList();
        if (rows.Count == 0 || context.GetService<IScrapedPriceRepository>() is not { } repo)
        {
            return 0;
        }

        try
        {
            await repo.AddRangeAsync(rows, ct);
            return rows.Count;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist {Count} crawled prices for {Store}", rows.Count, siteName);
            return 0;
        }
    }

    private static DateTimeOffset Now(ActivityExecutionContext context) =>
        context.GetService<ProfileOptions>()?.Now() ?? DateTimeOffset.UtcNow;
}

/// <summary>The minimal slice of a rendered page the crawl activities need (content + provider).</summary>
internal sealed record ScrapedPageLike(string Content, string Provider);
