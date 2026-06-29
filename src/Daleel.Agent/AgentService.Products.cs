using System.Text.Json;
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
    public async Task<ProductSearchResult> BuildProductSearchResultAsync(
        string query, GeoProfile geo, ResearchBundle bundle, string summary, CancellationToken cancellationToken,
        bool assessReputation = true, bool useLlmExtraction = true, SearchIntelligence? intelligence = null)
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
                // Brands & reviews are informational context — kept regardless of locality. A social /
                // forum / reference host (reddit, youtube, wikipedia…) is never a brand or a store, even
                // if the classifier mislabels it — surface it only as a review/discussion source.
                case ResultType.BrandPage when !IsNonCommerceHost(r.Url):
                    AddBrand(brands, r);
                    break;
                case ResultType.ReviewArticle:
                    reviews.Add(new ReviewSource { Title = r.Title, Url = r.Url, Snippet = r.Snippet, Source = r.Source });
                    break;

                // Buyable surfaces — only kept when confirmed local AND on an actual commerce host.
                case ResultType.StorePage when KeepLocal(r.Url, false) && !IsNonCommerceHost(r.Url):
                    AddStore(stores, r);
                    break;
                case ResultType.Marketplace when KeepLocal(r.Url, false) && !IsNonCommerceHost(r.Url):
                    AddMarketplace(marketplaces, r);
                    break;
                case ResultType.ProductListing when KeepLocal(r.Url, false) && !IsNonCommerceHost(r.Url):
                    webListings.Add(WebResultToListing(r, type));
                    break;

                // A mislabeled social/forum/reference hit that fell into a buyable bucket: keep it as a
                // discussion source rather than dropping it (so the data isn't lost), never as a store.
                case ResultType.StorePage or ResultType.Marketplace or ResultType.ProductListing
                    when IsNonCommerceHost(r.Url):
                    reviews.Add(new ReviewSource { Title = r.Title, Url = r.Url, Snippet = r.Snippet, Source = r.Source });
                    break;
            }
        }

        // Shopping hits come from a geo-targeted search (gl=cc) → treated as local.
        var shoppingListings = ListingExtractor.FromShopping(bundle.ShoppingResults);

        // Deep-extract individual listings from the top LOCAL marketplace/store pages.
        var extracted = await ExtractListingsAsync(classified, geo, includeIntl, cancellationToken).ConfigureAwait(false);

        // Merge the deterministic sources (most-detailed first) so duplicate listings collapse.
        var deterministic = ListingExtractor.Merge(extracted, shoppingListings, webListings);

        // Always-on LLM extraction: have the LLM read the gathered context and pull out concrete
        // products + per-store offers the structured parsers miss (markets where the shopping/scrape
        // APIs return thin or no data — the common case for e.g. "best ACs in Jordan"). The aggregator
        // turns every source — structured and LLM-extracted alike — into a price offer on the same model.
        var (llmListings, llmInsights) = useLlmExtraction
            ? await ExtractProductListingsAsync(query, geo, bundle, includeIntl, cancellationToken, intelligence?.Schema)
                .ConfigureAwait(false)
            : (Array.Empty<ProductListing>(),
               (IReadOnlyDictionary<string, ModelInsight>)new Dictionary<string, ModelInsight>(StringComparer.Ordinal));

        // De-duplicate the LLM offers against the deterministic ones (and each other) before
        // concatenating, so an offer the structured parsers already found isn't surfaced twice.
        // Identity is the URL when present (the strongest signal), else model-key + source +
        // price + currency — mirroring the aggregator's notion of "the same offer".
        static string OfferIdentity(ProductListing l) =>
            string.IsNullOrWhiteSpace(l.Url)
                ? $"{ListingExtractor.DedupKey(l)}|{l.Source?.Trim().ToLowerInvariant()}|{l.Price}|{l.Currency?.Trim().ToLowerInvariant()}"
                : $"u:{l.Url!.Trim().ToLowerInvariant()}";

        var seen = new HashSet<string>(deterministic.Select(OfferIdentity), StringComparer.Ordinal);
        var dedupedLlm = llmListings.Where(l => seen.Add(OfferIdentity(l)));

        var listings = deterministic
            .Concat(dedupedLlm)
            .OrderBy(l => l.Price ?? decimal.MaxValue)
            .ToList();

        var models = ListingAggregator.Aggregate(listings);

        // A product the LLM surfaced with no actionable offer becomes a single placeholder offer
        // (no price, no link) during aggregation — which would misleadingly read as "1 seller". Strip
        // those placeholders so such a model honestly reports 0 sellers while still being listed.
        models = models
            .Select(m => m.Offers.Any(IsPlaceholderOffer)
                ? m with { Offers = m.Offers.Where(o => !IsPlaceholderOffer(o)).ToList() }
                : m)
            .ToList();

        // Attach the LLM-distilled pros/cons + verdict to each model up-front (keyed the same way
        // the aggregator groups listings), so the grid card and detail panel can show an honest
        // summary without waiting for the per-model on-demand deep scrape.
        if (llmInsights.Count > 0)
        {
            models = models
                .Select(m => llmInsights.TryGetValue(ModelInsightKey(m), out var ins)
                    ? m with
                    {
                        Pros = m.Pros.Count > 0 ? m.Pros : ins.Pros,
                        Cons = m.Cons.Count > 0 ? m.Cons : ins.Cons,
                        ReviewSummary = string.IsNullOrWhiteSpace(m.ReviewSummary) ? ins.Summary : m.ReviewSummary
                    }
                    : m)
                .ToList();
        }

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
            .Select(g =>
            {
                var brandModels = models.Where(m => MatchesBrand(m, g.Key)).ToList();

                // Build the price range from EVERY priced offer across the brand's models (not just
                // each model's lowest, which understates the true high end). A min/max is only
                // meaningful when those offers share a single currency — so surface a range only when
                // exactly one distinct currency is present; otherwise leave it unset.
                var pricedOffers = brandModels
                    .SelectMany(m => m.Offers)
                    .Where(o => o.Price is not null && !string.IsNullOrWhiteSpace(o.Currency))
                    .ToList();
                var currencies = pricedOffers
                    .Select(o => o.Currency!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var prices = currencies.Count == 1
                    ? pricedOffers.Select(o => o.Price!.Value).ToList()
                    : new List<decimal>();
                var currency = currencies.Count == 1 ? currencies[0] : null;

                return new BrandInfo
                {
                    Name = g.Key,
                    ListingCount = brandModels.Count,
                    // Surface the actual models on offer (model number if known, else the product
                    // name) so the brand card answers "what can I buy?", not just "how many?".
                    Models = brandModels
                        .Select(m => string.IsNullOrWhiteSpace(m.Model) ? m.Name : m.Model!)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(6)
                        .ToList(),
                    PriceFrom = prices.Count > 0 ? new Money(prices.Min(), currency!) : null,
                    PriceTo = prices.Count > 0 ? new Money(prices.Max(), currency!) : null,
                    Reputation = FindReputation(reputations, g.Key),
                    Url = brands.Values.FirstOrDefault(bp => NameMatch(bp.Name, g.Key))?.Url
                };
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
            Schema = intelligence?.Schema ?? ProductSchema.General,
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
        // Skip the per-brand reputation pass AND the LLM extraction pass here — the detail panel is
        // about one model, and we keep the on-demand deep scrape lean (no extra LLM round-trips).
        var result = await BuildProductSearchResultAsync(model, geo, bundle, string.Empty, cancellationToken,
            assessReputation: false, useLlmExtraction: false).ConfigureAwait(false);

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
            .Take(_options.MaxListingUrls)
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

    /// <summary>
    /// LLM structured-extraction pass: reads the gathered context and pulls out concrete products
    /// with their per-store offers, flattened into one <see cref="ProductListing"/> per offer (all
    /// offers of a model share its brand+model, so the aggregator regroups them into one model with
    /// many price sources). Non-local offers are dropped unless the user asked for international.
    /// Returns empty on thin context or any failure, so the rest of the report is unaffected.
    /// </summary>
    private async Task<(IReadOnlyList<ProductListing> Listings, IReadOnlyDictionary<string, ModelInsight> Insights)>
        ExtractProductListingsAsync(
        string query, GeoProfile geo, ResearchBundle bundle, bool includeIntl, CancellationToken cancellationToken,
        ProductSchema? schema = null)
    {
        var empty = (
            (IReadOnlyList<ProductListing>)Array.Empty<ProductListing>(),
            (IReadOnlyDictionary<string, ModelInsight>)new Dictionary<string, ModelInsight>(StringComparer.Ordinal));

        // Classifying context: real product/store listings are separated from editorial articles, so the
        // extractor mines articles for which products exist without ever listing an article as an item.
        var context = BuildExtractionContext(bundle);
        if (context.Length == 0)
        {
            return empty;
        }

        // Ask the LLM for structured products; if its JSON can't be parsed, retry once before
        // falling back gracefully to no LLM listings (the deterministic sources still stand).
        var dto = await ExtractProductsDtoAsync(query, geo, context, cancellationToken, schema).ConfigureAwait(false);
        if (dto?.Products is null)
        {
            return empty;
        }

        var listings = new List<ProductListing>();
        var insights = new Dictionary<string, ModelInsight>(StringComparer.Ordinal);
        foreach (var p in dto.Products)
        {
            var name = string.IsNullOrWhiteSpace(p.Name) ? p.Model : p.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue; // nothing to identify the product by
            }

            var brand = string.IsNullOrWhiteSpace(p.Brand) ? null : p.Brand!.Trim();
            var model = string.IsNullOrWhiteSpace(p.Model) ? null : p.Model!.Trim();
            var image = string.IsNullOrWhiteSpace(p.ImageUrl) ? null : p.ImageUrl!.Trim();

            // Capture the model's distilled pros/cons/verdict (when the LLM provided them), keyed
            // the same way the aggregator groups listings, so they can be re-attached after grouping.
            var pros = CleanList(p.Pros);
            var cons = CleanList(p.Cons);
            var verdict = string.IsNullOrWhiteSpace(p.Summary) ? null : p.Summary!.Trim();
            if (pros.Count > 0 || cons.Count > 0 || verdict is not null)
            {
                insights[ModelInsightKey(brand, model, name!.Trim())] =
                    new ModelInsight(pros, cons, verdict);
            }
            var specs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (p.Specs is { Count: > 0 })
            {
                foreach (var (key, value) in p.Specs)
                {
                    if (!string.IsNullOrWhiteSpace(key) && SpecValue(value) is { Length: > 0 } sv)
                    {
                        specs[key] = sv;
                    }
                }
            }

            // Keep every offer that is local (or intl-allowed); a bare source name with no price and no
            // link still counts as an offer here — it tells the shopper a seller exists even before the
            // deep-dive scrapes a price. Non-local linked offers are dropped, honouring the same locality
            // rule as the deterministic path. The ARTICLE the model was found in is never a product: the
            // LLM is instructed to mine articles for which products exist but never to output the article
            // itself, and review/buying-guide hits arrive separately as Reviews, not as product DTOs.
            var localOffers = (p.Offers ?? new List<ExtractedOfferDto>())
                .Select(o => (o, url: string.IsNullOrWhiteSpace(o.Url) ? null : o.Url!.Trim()))
                .Where(t => t.url is null || includeIntl ||
                            LocalityClassifier.IsLocal(t.url, geo.CountryCode, geo.Country))
                .Select(t => (t.o, t.url, price: ParsePrice(t.o.Price)))
                .ToList();

            foreach (var (o, url, price) in localOffers)
            {
                listings.Add(new ProductListing
                {
                    Name = name!.Trim(),
                    Brand = brand,
                    Model = model,
                    Price = price,
                    Currency = string.IsNullOrWhiteSpace(o.Currency)
                        ? (price is not null ? geo.Currency : null)
                        : o.Currency!.Trim(),
                    Url = url,
                    ImageUrl = image,
                    Source = string.IsNullOrWhiteSpace(o.Source) ? (brand ?? "Search") : o.Source!.Trim(),
                    SourceType = ResultType.Marketplace,
                    Specs = specs,
                    Condition = ListingExtractor.NormalizeCondition(o.Condition),
                });
            }

            // A model with no usable offers (none at all, or all of them non-local) is still surfaced as a
            // name-only item — brand/model/specs/image visible — so a shopper researching "best <category>"
            // sees the option exists even when no in-context seller carried a price or a local link. The
            // deep-dive enrichment fills the price/source afterwards; dropping it here is what made
            // article-heavy queries return only 2-3 items.
            if (localOffers.Count == 0)
            {
                listings.Add(new ProductListing
                {
                    Name = name!.Trim(),
                    Brand = brand,
                    Model = model,
                    ImageUrl = image,
                    Source = brand ?? "Search",
                    SourceType = ResultType.Marketplace,
                    Specs = specs,
                });
            }
        }

        return (listings, insights);
    }

    /// <summary>An LLM-distilled verdict for a model: short pros/cons and a one-line summary.</summary>
    private sealed record ModelInsight(
        IReadOnlyList<string> Pros, IReadOnlyList<string> Cons, string? Summary);

    /// <summary>
    /// Insight-map key matching <see cref="ListingExtractor.DedupKey"/>'s identity rules
    /// (brand+model, else normalized name), so a model aggregated from listings can look up the
    /// insight extracted for the same product.
    /// </summary>
    private static string ModelInsightKey(string? brand, string? model, string name) =>
        ListingExtractor.DedupKey(new ProductListing { Brand = brand, Model = model, Name = name });

    private static string ModelInsightKey(ProductModel m) =>
        ListingExtractor.DedupKey(new ProductListing { Brand = m.Brand, Model = m.Model, Name = m.Name });

    /// <summary>Trims, drops blanks and de-dupes a possibly-null LLM string list.</summary>
    private static IReadOnlyList<string> CleanList(IEnumerable<string>? items) =>
        items is null
            ? Array.Empty<string>()
            : items.Where(s => !string.IsNullOrWhiteSpace(s))
                   .Select(s => s.Trim())
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .ToList();

    /// <summary>
    /// Calls the LLM extraction prompt and parses its JSON response, retrying once when the
    /// model returns something we can't parse into the expected shape. Returns null when both
    /// attempts fail, so the caller can fall back gracefully to no LLM-extracted listings.
    /// </summary>
    private async Task<ExtractedProductsDto?> ExtractProductsDtoAsync(
        string query, GeoProfile geo, string context, CancellationToken cancellationToken, ProductSchema? schema = null)
    {
        var prompt = PromptTemplates.ExtractProducts(query, geo, context, schema);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var text = await _llm.CompleteTextAsync(
                    PromptTemplates.ProductExtractionSystem, prompt, cancellationToken).ConfigureAwait(false);

                var dto = LlmJson.Deserialize<ExtractedProductsDto>(text);
                if (dto?.Products is not null)
                {
                    return dto;
                }

                Log($"product extraction returned unparseable JSON (attempt {attempt}/2)");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"product extraction failed (attempt {attempt}/2): {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// An offer with neither a price nor a link isn't actionable — there's no seller to send the
    /// shopper to. These come from products the LLM surfaced without any concrete offer.
    /// </summary>
    private static bool IsPlaceholderOffer(PriceOffer o) =>
        o.Price is null && string.IsNullOrWhiteSpace(o.Url);

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

    /// <summary>
    /// Social networks, forums, video, and reference sites — never a store, brand, or buyable listing,
    /// even when a result classifier mislabels one (e.g. a reddit thread tagged as a "store"). Matches
    /// the registrable host and any subdomain (jo.reddit.com, m.facebook.com…).
    /// </summary>
    private static readonly string[] NonCommerceHosts =
    {
        "reddit.com", "youtube.com", "youtu.be", "facebook.com", "twitter.com", "x.com",
        "instagram.com", "tiktok.com", "pinterest.com", "linkedin.com", "wikipedia.org",
        "quora.com", "medium.com", "blogspot.com", "wordpress.com", "tumblr.com", "threads.net",
        "telegram.org", "t.me", "whatsapp.com", "snapchat.com",
    };

    /// <summary>True when the URL's host is a known non-commerce site (so it must not become a store/brand).</summary>
    public static bool IsNonCommerceHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return NonCommerceHosts.Any(h =>
            host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>Wire shape for the structured product-extraction LLM output.</summary>
    private sealed class ExtractedProductsDto
    {
        [JsonPropertyName("products")] public List<ExtractedProductDto>? Products { get; set; }
    }

    private sealed class ExtractedProductDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("brand")] public string? Brand { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("imageUrl")] public string? ImageUrl { get; set; }

        // Tolerant of mixed value types: LLMs emit spec values as strings, numbers or bools.
        [JsonPropertyName("specs")] public Dictionary<string, JsonElement>? Specs { get; set; }
        [JsonPropertyName("offers")] public List<ExtractedOfferDto>? Offers { get; set; }

        // Optional per-model verdict distilled from the reviews/opinions in the context.
        [JsonPropertyName("pros")] public List<string>? Pros { get; set; }
        [JsonPropertyName("cons")] public List<string>? Cons { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
    }

    private sealed class ExtractedOfferDto
    {
        [JsonPropertyName("source")] public string? Source { get; set; }

        // Tolerant of a number (320) or a string ("320 JOD") — parsed via <see cref="ParsePrice"/>.
        [JsonPropertyName("price")] public JsonElement Price { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("condition")] public string? Condition { get; set; }
    }

    /// <summary>Parses an LLM price element that may be a JSON number or a string like "320 JOD".</summary>
    private static decimal? ParsePrice(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number when el.TryGetDecimal(out var d) => d,
        JsonValueKind.String => ParsePriceString(el.GetString()),
        _ => null
    };

    private static decimal? ParsePriceString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var digits = new string(raw.Where(c => char.IsDigit(c) || c is '.' or ',').ToArray()).Replace(",", string.Empty);
        return decimal.TryParse(digits, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    /// <summary>Renders a spec <see cref="JsonElement"/> (string/number/bool) as a display string.</summary>
    private static string? SpecValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString()?.Trim(),
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

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
