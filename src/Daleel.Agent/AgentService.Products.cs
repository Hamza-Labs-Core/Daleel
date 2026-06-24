using System.Text.Json.Serialization;
using Daleel.Core.Geo;
using Daleel.Core.Intelligence;
using Daleel.Core.Llm;
using Daleel.Core.Models;
using Daleel.Pipeline.Extraction;
using Daleel.Search.Abstractions;

namespace Daleel.Agent;

/// <summary>
/// The "product search" projection of <see cref="AgentService"/>: turning a gathered
/// <see cref="ResearchBundle"/> into a structured, link-rich <see cref="ProductSearchResult"/>
/// — models aggregated across all their price sources, plus the brands/stores/marketplaces/
/// reviews behind them. Buyable surfaces (listings, stores, marketplaces) are restricted to
/// sources confirmed local to the market unless the user explicitly asked for international.
/// </summary>
public sealed partial class AgentService
{
    /// <summary>
    /// Researches a product query and returns structured, actionable models rather than a
    /// pure narrative. Used for queries classified as <see cref="QueryType.ProductResearch"/>.
    /// </summary>
    public async Task<ProductSearchResult> SearchProductsAsync(
        string query, string? geoKey = null, CancellationToken cancellationToken = default)
    {
        var geo = GeoProfiles.ResolveOrDefault(geoKey ?? _options.DefaultGeo);
        var strategy = await PlanAsync(PromptTemplates.PlanProduct(query, geo), cancellationToken).ConfigureAwait(false);
        var bundle = await GatherAsync(strategy, geo, cancellationToken).ConfigureAwait(false);
        var summary = await AnalyzeAsync($"Products available: {query} in {geo.Country}", geo, bundle, cancellationToken,
            PromptTemplates.ProductAnalystSystem).ConfigureAwait(false);

        return await BuildProductSearchResultAsync(query, geo, bundle, summary, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Projects a gathered bundle into a <see cref="ProductSearchResult"/>: classifies every
    /// web hit, drops non-local buyable sources, extracts/normalizes listings, aggregates them
    /// into per-model entries (one model, many price sources), and builds comparison tiers.
    /// </summary>
    internal async Task<ProductSearchResult> BuildProductSearchResultAsync(
        string query, GeoProfile geo, ResearchBundle bundle, string summary, CancellationToken cancellationToken)
    {
        var cc = geo.CountryCode;
        var countryName = geo.Country;
        var includeIntl = LocalityClassifier.QueryWantsInternational(query);

        bool KeepLocal(string? url, bool geoTargeted) =>
            includeIntl || LocalityClassifier.IsLocal(url, cc, countryName, geoTargeted);

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
                // Brands & reviews are informational context — kept regardless of locality.
                case ResultType.BrandPage:
                    AddBrand(brands, r);
                    break;
                case ResultType.ReviewArticle:
                    reviews.Add(new ReviewSource { Title = r.Title, Url = r.Url, Snippet = r.Snippet, Source = r.Source });
                    break;

                // Buyable surfaces — only kept when confirmed local.
                case ResultType.StorePage when KeepLocal(r.Url, false):
                    AddStore(stores, r);
                    break;
                case ResultType.Marketplace when KeepLocal(r.Url, false):
                    AddMarketplace(marketplaces, r);
                    break;
                case ResultType.ProductListing when KeepLocal(r.Url, false):
                    webListings.Add(WebResultToListing(r, type));
                    break;
            }
        }

        // Shopping hits come from a geo-targeted search (gl=cc) → treated as local.
        var shoppingListings = ListingExtractor.FromShopping(bundle.ShoppingResults);

        // Deep-extract individual listings from the top LOCAL marketplace/store pages.
        var extracted = await ExtractListingsAsync(classified, geo, includeIntl, cancellationToken).ConfigureAwait(false);

        // Merge (most-detailed first) then aggregate into one entry per model.
        var listings = ListingExtractor.Merge(extracted, shoppingListings, webListings)
            .OrderBy(l => l.Price ?? decimal.MaxValue)
            .ToList();

        var models = ListingAggregator.Aggregate(listings);

        // Comparison tiers off a representative (cheapest) listing per model.
        var representatives = models.Select(ToRepresentative).ToList();
        var comparisons = ComparisonGrouper.Group(representatives);

        // Backfill brand counts from the aggregated models.
        var brandsWithCounts = brands.Values
            .Select(b => b with { ListingCount = models.Count(m => MatchesBrand(m, b.Name)) })
            .OrderByDescending(b => b.ListingCount)
            .ThenBy(b => b.Name)
            .ToList();

        // Google-Places stores are inherently local.
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
            Country = geo.Country,
            Summary = summary,
            Models = models,
            Listings = listings,
            IncludeInternational = includeIntl,
            Brands = brandsWithCounts,
            Stores = stores.Values.ToList(),
            Reviews = reviews,
            Marketplaces = marketplaces.Values.OrderByDescending(m => m.ListingCount).ToList(),
            Comparisons = comparisons,
            GeneratedAt = _options.Clock()
        };
    }

    /// <summary>
    /// Per-model deep scrape: runs a focused, exact-model search across local sources, aggregates
    /// every priced offer, and enriches the model with an LLM-distilled pros/cons verdict. Returns
    /// null when no matching model is found. Used on-demand when the user opens a model's detail panel.
    /// </summary>
    public async Task<ProductModel?> ResearchModelAsync(
        string model, string? geoKey = null, CancellationToken cancellationToken = default)
    {
        var geo = GeoProfiles.ResolveOrDefault(geoKey ?? _options.DefaultGeo);
        Log($"Deep-scanning model: {model} [{geo.Country}]");

        var strategy = await PlanAsync(PromptTemplates.PlanModel(model, geo), cancellationToken).ConfigureAwait(false);
        var bundle = await GatherAsync(strategy, geo, cancellationToken).ConfigureAwait(false);
        var result = await BuildProductSearchResultAsync(model, geo, bundle, string.Empty, cancellationToken)
            .ConfigureAwait(false);

        var best = PickBestModel(result.Models, model);
        if (best is null)
        {
            return null;
        }

        return await EnrichModelAsync(best, bundle, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds an LLM pros/cons verdict (and review summary) to a model from gathered context.</summary>
    private async Task<ProductModel> EnrichModelAsync(
        ProductModel model, ResearchBundle bundle, CancellationToken cancellationToken)
    {
        var context = BuildContext(bundle);
        if (context.Length == 0)
        {
            return model;
        }

        try
        {
            var text = await _llm.CompleteTextAsync(
                PromptTemplates.ModelDetailSystem,
                PromptTemplates.ModelProsCons(model.Name, context),
                cancellationToken).ConfigureAwait(false);

            var dto = LlmJson.Deserialize<ProsConsDto>(text);
            if (dto is not null)
            {
                return model with
                {
                    Pros = dto.Pros ?? model.Pros.ToList(),
                    Cons = dto.Cons ?? model.Cons.ToList(),
                    ReviewSummary = string.IsNullOrWhiteSpace(dto.Summary) ? model.ReviewSummary : dto.Summary
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"model enrichment failed: {ex.Message}");
        }

        return model;
    }

    /// <summary>Picks the model whose name/model best matches the query (most shared tokens), else the most-sourced.</summary>
    private static ProductModel? PickBestModel(IReadOnlyList<ProductModel> models, string query)
    {
        if (models.Count == 0)
        {
            return null;
        }

        var tokens = query.ToLowerInvariant().Split(new[] { ' ', '-', '/' }, StringSplitOptions.RemoveEmptyEntries);

        return models
            .OrderByDescending(m =>
            {
                var hay = $"{m.Brand} {m.Model} {m.Name}".ToLowerInvariant();
                return tokens.Count(t => t.Length > 1 && hay.Contains(t, StringComparison.Ordinal));
            })
            .ThenByDescending(m => m.SellerCount)
            .First();
    }

    private async Task<IReadOnlyList<ProductListing>> ExtractListingsAsync(
        IReadOnlyList<(SearchResult r, ResultType type)> classified, GeoProfile geo, bool includeIntl,
        CancellationToken cancellationToken)
    {
        if (_scraper is not IExtractProvider extractor)
        {
            return Array.Empty<ProductListing>();
        }

        var targets = classified
            .Where(c => c.type is ResultType.Marketplace or ResultType.StorePage && !string.IsNullOrWhiteSpace(c.r.Url))
            .Where(c => includeIntl || LocalityClassifier.IsLocal(c.r.Url, geo.CountryCode, geo.Country))
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

    private static ProductListing ToRepresentative(ProductModel m)
    {
        var offer = m.LowestOffer;
        return new ProductListing
        {
            Name = m.Name,
            Brand = m.Brand,
            Model = m.Model,
            Price = m.LowestPrice,
            Currency = offer?.Currency,
            Url = offer?.Url,
            ImageUrl = m.ImageUrl,
            Source = offer?.Source,
            SourceType = offer?.SourceType ?? ResultType.Unknown,
            Specs = m.Specs,
            Condition = offer?.Condition
        };
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

    private static bool MatchesBrand(ProductModel m, string brand) =>
        (m.Brand?.Contains(brand, StringComparison.OrdinalIgnoreCase) ?? false) ||
        m.Name.Contains(brand, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Wire shape for the model pros/cons LLM output.</summary>
    private sealed class ProsConsDto
    {
        [JsonPropertyName("pros")] public List<string>? Pros { get; set; }
        [JsonPropertyName("cons")] public List<string>? Cons { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
    }
}
