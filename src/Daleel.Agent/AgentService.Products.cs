using Daleel.Core.Geo;
using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Pipeline.Extraction;
using Daleel.Search.Abstractions;

namespace Daleel.Agent;

/// <summary>
/// The "product search" projection of <see cref="AgentService"/>: turning a gathered
/// <see cref="ResearchBundle"/> into a structured, link-rich <see cref="ProductSearchResult"/>
/// — actual listings, plus the brands/stores/marketplaces/reviews behind them, plus ready
/// comparison tiers. Kept in its own partial so the core agent file stays focused.
/// </summary>
public sealed partial class AgentService
{
    /// <summary>
    /// Researches a product query and returns structured, actionable listings rather than a
    /// pure narrative. Used for queries classified as <see cref="QueryType.ProductResearch"/>.
    /// </summary>
    public async Task<ProductSearchResult> SearchProductsAsync(
        string query, string? geoKey = null, CancellationToken cancellationToken = default)
    {
        var geo = GeoProfiles.ResolveOrDefault(geoKey ?? _options.DefaultGeo);
        var strategy = await PlanAsync(PromptTemplates.PlanProduct(query, geo), cancellationToken).ConfigureAwait(false);
        var bundle = await GatherAsync(strategy, geo, cancellationToken).ConfigureAwait(false);
        var summary = await AnalyzeAsync($"Products available: {query} in {geo.Country}", geo, bundle, cancellationToken)
            .ConfigureAwait(false);

        return await BuildProductSearchResultAsync(query, geo, bundle, summary, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Projects a gathered bundle into a <see cref="ProductSearchResult"/>: classifies every
    /// web hit into its bucket, extracts/normalizes listings (shopping API + AI Extract of
    /// top marketplace pages), then de-dupes, sorts by price, and builds comparison tiers.
    /// </summary>
    internal async Task<ProductSearchResult> BuildProductSearchResultAsync(
        string query, GeoProfile geo, ResearchBundle bundle, string summary, CancellationToken cancellationToken)
    {
        // 1) Classify web results so each lands in the right bucket.
        var classified = bundle.WebResults
            .Select(r => (r, type: ResultClassifier.Classify(r.Url, r.Title, r.Snippet)))
            .ToList();

        var brands = new Dictionary<string, BrandInfo>(StringComparer.OrdinalIgnoreCase);
        var stores = new Dictionary<string, StoreInfo>(StringComparer.OrdinalIgnoreCase);
        var marketplaces = new Dictionary<string, MarketplaceLink>(StringComparer.OrdinalIgnoreCase);
        var reviews = new List<ReviewSource>();
        var webListings = new List<ProductListing>();

        foreach (var (r, type) in classified)
        {
            switch (type)
            {
                case ResultType.BrandPage:
                    AddBrand(brands, r);
                    break;
                case ResultType.StorePage:
                    AddStore(stores, r);
                    break;
                case ResultType.Marketplace:
                    AddMarketplace(marketplaces, r);
                    break;
                case ResultType.ReviewArticle:
                    reviews.Add(new ReviewSource { Title = r.Title, Url = r.Url, Snippet = r.Snippet, Source = r.Source });
                    break;
                case ResultType.ProductListing:
                    webListings.Add(WebResultToListing(r, type));
                    break;
            }
        }

        // 2) Structured shopping hits → listings.
        var shoppingListings = ListingExtractor.FromShopping(bundle.ShoppingResults);

        // 3) Deep-extract individual listings from the top marketplace/store pages (when a
        //    schema-extracting scraper is configured).
        var extracted = await ExtractListingsAsync(classified, geo, cancellationToken).ConfigureAwait(false);

        // Merge most-detailed first (extracted → shopping → web) so model-keyed dedup wins.
        var listings = ListingExtractor.Merge(extracted, shoppingListings, webListings)
            .OrderBy(l => l.Price ?? decimal.MaxValue)
            .ToList();

        // Backfill brand counts now that listings are known.
        var withCounts = brands.Values
            .Select(b => b with { ListingCount = listings.Count(l => MatchesBrand(l, b.Name)) })
            .OrderByDescending(b => b.ListingCount)
            .ThenBy(b => b.Name)
            .ToList();

        // Fold Google-Places stores into the store directory.
        foreach (var s in bundle.Stores)
        {
            var key = s.Website ?? s.Name;
            stores.TryAdd(key, new StoreInfo
            {
                Name = s.Name,
                Url = s.Website,
                Address = s.Address,
                Phone = s.Phone,
                IsOnline = false
            });
        }

        return new ProductSearchResult
        {
            Query = query,
            Geo = geo.Key,
            Summary = summary,
            Listings = listings,
            Brands = withCounts,
            Stores = stores.Values.ToList(),
            Reviews = reviews,
            Marketplaces = marketplaces.Values.OrderByDescending(m => m.ListingCount).ToList(),
            Comparisons = ComparisonGrouper.Group(listings),
            GeneratedAt = _options.Clock()
        };
    }

    private async Task<IReadOnlyList<ProductListing>> ExtractListingsAsync(
        IReadOnlyList<(SearchResult r, ResultType type)> classified, GeoProfile geo, CancellationToken cancellationToken)
    {
        if (_scraper is not IExtractProvider extractor)
        {
            return Array.Empty<ProductListing>();
        }

        // Prefer marketplace category pages and multi-product store pages — those hold many listings.
        var targets = classified
            .Where(c => c.type is ResultType.Marketplace or ResultType.StorePage && !string.IsNullOrWhiteSpace(c.r.Url))
            .Select(c => (c.r.Url!, Source: SourceName(c.r), c.type))
            .Take(_options.MaxUrlsToRead)
            .ToList();

        if (targets.Count == 0)
        {
            return Array.Empty<ProductListing>();
        }

        var tasks = targets.Select(t =>
            ListingExtractor.ExtractAsync(extractor, t.Item1, t.Source, t.type, geo.Currency, cancellationToken));
        var batches = await Task.WhenAll(tasks).ConfigureAwait(false);
        return batches.SelectMany(b => b).ToList();
    }

    private static void AddBrand(IDictionary<string, BrandInfo> brands, SearchResult r)
    {
        var name = BrandNameFrom(r);
        if (string.IsNullOrWhiteSpace(name)) return;
        brands.TryAdd(name, new BrandInfo { Name = name, Url = r.Url });
    }

    private static void AddStore(IDictionary<string, StoreInfo> stores, SearchResult r)
    {
        var name = SourceName(r);
        stores.TryAdd(name, new StoreInfo { Name = name, Url = r.Url, IsOnline = true });
    }

    private static void AddMarketplace(IDictionary<string, MarketplaceLink> marketplaces, SearchResult r)
    {
        var name = SourceName(r);
        if (marketplaces.TryGetValue(name, out var existing))
        {
            marketplaces[name] = existing with { ListingCount = existing.ListingCount + 1 };
        }
        else
        {
            marketplaces[name] = new MarketplaceLink { Name = name, Url = r.Url, ListingCount = 1 };
        }
    }

    private static ProductListing WebResultToListing(SearchResult r, ResultType type) => new()
    {
        Name = r.Title,
        Price = r.Price?.Amount,
        Currency = r.Price?.Currency,
        Url = r.Url,
        Source = SourceName(r),
        Seller = r.Seller,
        SourceType = type
    };

    private static bool MatchesBrand(ProductListing l, string brand) =>
        (l.Brand?.Contains(brand, StringComparison.OrdinalIgnoreCase) ?? false) ||
        l.Name.Contains(brand, StringComparison.OrdinalIgnoreCase);

    /// <summary>A human label for a result's source: registrable host label, else the title.</summary>
    private static string SourceName(SearchResult r)
    {
        var host = HostLabel(r.Url);
        return host ?? (string.IsNullOrWhiteSpace(r.Seller) ? r.Title : r.Seller!);
    }

    /// <summary>Best-effort brand name: the host label for a brand domain, else the title's first token.</summary>
    private static string BrandNameFrom(SearchResult r)
    {
        var host = HostLabel(r.Url);
        if (!string.IsNullOrWhiteSpace(host))
        {
            return host!;
        }

        var first = r.Title.Trim().Split(' ', '-', '|').FirstOrDefault();
        return first ?? r.Title;
    }

    /// <summary>Title-cased registrable label of a host, e.g. "https://www.samsung.com/jo" → "Samsung".</summary>
    private static string? HostLabel(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return null;
        }

        var host = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
        var label = host.Split('.').FirstOrDefault();
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        return char.ToUpperInvariant(label[0]) + label[1..];
    }
}
