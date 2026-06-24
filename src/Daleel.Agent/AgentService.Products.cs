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
        string query, GeoProfile geo, ResearchBundle bundle, string summary, CancellationToken cancellationToken,
        bool assessReputation = true)
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

        // Follow-up step: assess each brand's reputation in-market (reliability, local service,
        // warranty) so a cheap product from an unsupported brand can be flagged.
        var reputations = assessReputation
            ? await AssessBrandReputationsAsync(models, geo, bundle, cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, BrandReputation>(StringComparer.OrdinalIgnoreCase);

        if (reputations.Count > 0)
        {
            models = models
                .Select(m => m.Brand is { Length: > 0 } b && FindReputation(reputations, b) is { } rep
                    ? m with { BrandReputation = rep }
                    : m)
                .ToList();
        }

        // Comparison tiers off a representative (cheapest) listing per model.
        var representatives = models.Select(ToRepresentative).ToList();
        var comparisons = ComparisonGrouper.Group(representatives);

        bool NameMatch(string a, string b) =>
            a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
            a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
            b.Contains(a, StringComparison.OrdinalIgnoreCase);

        // Brands are the actual product MANUFACTURERS (Samsung, LG, Gree…), drawn from the
        // aggregated models — never the stores/marketplaces that sell them. Attach in-market
        // reputation, and an official catalog URL when a classified brand page matches.
        var brandsWithCounts = models
            .Select(m => m.Brand)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b!.Trim())
            .GroupBy(b => b, StringComparer.OrdinalIgnoreCase)
            .Select(g => new BrandInfo
            {
                Name = g.Key,
                ListingCount = models.Count(m => MatchesBrand(m, g.Key)),
                Reputation = FindReputation(reputations, g.Key),
                Url = brands.Values.FirstOrDefault(bp => NameMatch(bp.Name, g.Key))?.Url
            })
            .OrderByDescending(b => b.ListingCount)
            .ThenBy(b => b.Name)
            .ToList();

        // Any "brand page" we classified that ISN'T one of our manufacturers is really a seller's
        // site (Amazon, OpenSooq, Smart Buy). Surface it under Stores — never under Brands.
        foreach (var bp in brands.Values)
        {
            if (brandsWithCounts.Any(b => NameMatch(b.Name, bp.Name)) || !KeepLocal(bp.Url, false))
            {
                continue;
            }

            stores.TryAdd(bp.Name, new StoreInfo { Name = bp.Name, Url = bp.Url, IsOnline = true });
        }

        // Google-Places stores are inherently local — keep their star ratings and individual
        // reviews so the UI can show genuine user reviews (not editorial articles).
        foreach (var s in bundle.Stores)
        {
            var key = s.Website ?? s.Name;
            stores.TryAdd(key, new StoreInfo
            {
                Name = s.Name,
                Url = s.Website,
                Address = s.Address,
                Phone = s.Phone,
                IsOnline = false,
                Rating = s.Rating,
                ReviewCount = s.ReviewCount,
                Reviews = s.Reviews
            });
        }

        // Aggregate the per-brand social/forum opinions into one product-level "what people say"
        // feed, deduped by quote, so real user sentiment surfaces in the results (not just in the
        // brand detail panel).
        var socialReviews = models
            .Select(m => m.BrandReputation?.Social)
            .Where(sp => sp is { HasReviews: true })
            .SelectMany(sp => sp!.Reviews)
            .GroupBy(r => r.Quote, StringComparer.OrdinalIgnoreCase)
            .Select(grp => grp.First())
            .Take(12)
            .ToList();
        var social = socialReviews.Count > 0 ? new SocialProof { Reviews = socialReviews } : null;

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
            Social = social,
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
        // Skip the per-brand reputation pass here — the detail panel is about one model, and we
        // keep the on-demand deep scrape lean.
        var result = await BuildProductSearchResultAsync(model, geo, bundle, string.Empty, cancellationToken,
            assessReputation: false).ConfigureAwait(false);

        var best = PickBestModel(result.Models, model);
        if (best is null)
        {
            return null;
        }

        return await EnrichModelAsync(best, bundle, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Assesses the reputation of every brand in the result (one batched LLM call over the
    /// gathered context), focusing on reliability, local after-sales service, and warranty.
    /// Returns an empty map on any failure so the rest of the report is unaffected.
    /// </summary>
    private async Task<Dictionary<string, BrandReputation>> AssessBrandReputationsAsync(
        IReadOnlyList<ProductModel> models, GeoProfile geo, ResearchBundle bundle, CancellationToken cancellationToken)
    {
        var brands = models
            .Select(m => m.Brand)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        var empty = new Dictionary<string, BrandReputation>(StringComparer.OrdinalIgnoreCase);
        if (brands.Count == 0)
        {
            return empty;
        }

        var context = BuildContext(bundle);
        try
        {
            var text = await _llm.CompleteTextAsync(
                PromptTemplates.BrandReputationSystem,
                PromptTemplates.BrandReputations(brands, geo, context),
                cancellationToken).ConfigureAwait(false);

            var dto = LlmJson.Deserialize<BrandReputationsDto>(text);
            if (dto?.Brands is null)
            {
                return empty;
            }

            var map = new Dictionary<string, BrandReputation>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in dto.Brands)
            {
                if (string.IsNullOrWhiteSpace(b.Brand))
                {
                    continue;
                }

                map[b.Brand.Trim()] = new BrandReputation
                {
                    Brand = b.Brand.Trim(),
                    Score = b.Score,
                    Pros = b.Pros ?? new List<string>(),
                    Complaints = b.Complaints ?? new List<string>(),
                    HasLocalService = b.HasLocalService,
                    ServiceNote = b.ServiceNote,
                    Warranty = b.Warranty,
                    Summary = b.Summary,
                    Social = ToSocialProof(b.Reviews)
                };
            }

            return map;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"brand reputation assessment failed: {ex.Message}");
            return empty;
        }
    }

    /// <summary>Maps review DTOs into a <see cref="SocialProof"/>, or null when there are none.</summary>
    private static SocialProof? ToSocialProof(List<UserReviewDto>? reviews)
    {
        if (reviews is null || reviews.Count == 0)
        {
            return null;
        }

        var mapped = reviews
            .Where(r => !string.IsNullOrWhiteSpace(r.Quote))
            .Select(r => new UserReview
            {
                Quote = r.Quote!.Trim(),
                OriginalText = r.OriginalText,
                Source = r.Source,
                Url = r.Url,
                Sentiment = ParseSentiment(r.Sentiment),
                Date = DateTimeOffset.TryParse(r.Date, out var d) ? d : null,
                Language = r.Language
            })
            .ToList();

        return mapped.Count > 0 ? new SocialProof { Reviews = mapped } : null;
    }

    private static Sentiment ParseSentiment(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "positive" or "pos" => Sentiment.Positive,
        "negative" or "neg" => Sentiment.Negative,
        _ => Sentiment.Neutral
    };

    /// <summary>Finds a brand's reputation by exact or fuzzy (contains) name match.</summary>
    private static BrandReputation? FindReputation(IReadOnlyDictionary<string, BrandReputation> map, string brand)
    {
        if (map.Count == 0 || string.IsNullOrWhiteSpace(brand))
        {
            return null;
        }

        if (map.TryGetValue(brand, out var exact))
        {
            return exact;
        }

        return map.FirstOrDefault(kv =>
            kv.Key.Contains(brand, StringComparison.OrdinalIgnoreCase) ||
            brand.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)).Value;
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

    /// <summary>Wire shape for the batched brand-reputation LLM output.</summary>
    private sealed class BrandReputationsDto
    {
        [JsonPropertyName("brands")] public List<BrandReputationDto>? Brands { get; set; }
    }

    private sealed class BrandReputationDto
    {
        [JsonPropertyName("brand")] public string? Brand { get; set; }
        [JsonPropertyName("score")] public double? Score { get; set; }
        [JsonPropertyName("pros")] public List<string>? Pros { get; set; }
        [JsonPropertyName("complaints")] public List<string>? Complaints { get; set; }
        [JsonPropertyName("hasLocalService")] public bool? HasLocalService { get; set; }
        [JsonPropertyName("serviceNote")] public string? ServiceNote { get; set; }
        [JsonPropertyName("warranty")] public string? Warranty { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("reviews")] public List<UserReviewDto>? Reviews { get; set; }
    }

    private sealed class UserReviewDto
    {
        [JsonPropertyName("quote")] public string? Quote { get; set; }
        [JsonPropertyName("originalText")] public string? OriginalText { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("sentiment")] public string? Sentiment { get; set; }
        [JsonPropertyName("date")] public string? Date { get; set; }
        [JsonPropertyName("language")] public string? Language { get; set; }
    }
}
