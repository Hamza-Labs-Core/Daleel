using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
        bool assessReputation = true, bool useLlmExtraction = true, SearchIntelligence? intelligence = null,
        SearchIntentType intent = SearchIntentType.Product)
    {
        var cc = geo.CountryCode;
        var countryName = geo.Country;
        var includeIntl = LocalityClassifier.QueryWantsInternational(query);

        // Web results were fetched with gl={cc} whenever the market is known (SerpApiProvider),
        // so the classifier may apply its geo-scoped generic-gTLD rule when the result's own text
        // names the market: Google already constrained these hits, and a "…Jordan/JO…" title or
        // snippet is what separates jo-cell.com from a global seller that merely ranks here.
        var geoScopedSearch = !string.IsNullOrWhiteSpace(cc);

        bool KeepLocal(string? url, bool geoTargeted, string? evidence = null) =>
            includeIntl || LocalityClassifier.IsLocal(url, cc, countryName, geoTargeted, geoScopedSearch, evidence);

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
                case ResultType.StorePage when KeepLocal(r.Url, false, $"{r.Title} {r.Snippet}") && !IsNonCommerceHost(r.Url):
                    AddStore(stores, r);
                    break;
                case ResultType.Marketplace when KeepLocal(r.Url, false, $"{r.Title} {r.Snippet}") && !IsNonCommerceHost(r.Url):
                    AddMarketplace(marketplaces, r);
                    break;
                // A web hit whose title is a URL/bare domain has no product identity — never surface
                // a raw link as a product card (same rule as PickName and the extractor paths).
                case ResultType.ProductListing when KeepLocal(r.Url, false, $"{r.Title} {r.Snippet}") && !IsNonCommerceHost(r.Url)
                    && !LooksLikeUrl(r.Title):
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

        // FUNNEL VISIBILITY: "are we getting all the results?" must be answerable from a job's log,
        // not taken on faith. One line: what came in, where it landed, and why drops dropped.
        {
            var buyable = classified.Where(c => c.type is ResultType.StorePage or ResultType.Marketplace
                or ResultType.ProductListing).ToList();
            var droppedNonCommerce = buyable.Count(c => IsNonCommerceHost(c.r.Url));
            var droppedNonLocal = buyable.Count(c =>
                !IsNonCommerceHost(c.r.Url) && !KeepLocal(c.r.Url, false, $"{c.r.Title} {c.r.Snippet}"));
            var droppedUrlTitle = classified.Count(c => c.type == ResultType.ProductListing &&
                !IsNonCommerceHost(c.r.Url) && LooksLikeUrl(c.r.Title));
            Log($"result funnel: {classified.Count} web results → stores {stores.Count}, " +
                $"marketplaces {marketplaces.Count}, listings {webListings.Count}, brands {brands.Count}, " +
                $"reviews/articles {reviews.Count}; dropped {droppedNonLocal} non-local, " +
                $"{droppedNonCommerce} non-commerce(demoted), {droppedUrlTitle} url-titled; " +
                $"shopping {bundle.ShoppingResults.Count}, places-stores {bundle.Stores.Count}");
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
            ? await ExtractProductListingsAsync(query, geo, bundle, includeIntl, cancellationToken, intelligence?.Schema, intent)
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

        // Relevance gate: deterministic shopping hits (SerpAPI → listing) reach this grid WITHOUT any
        // LLM pass or halal guard, so loosely-matching items survive — a milk frother or a "slimming
        // coffee" drink in a coffee-MAKER search. One cheap LLM call asks, per item, "is this ITSELF
        // a <target>?" and drops the ones that aren't. Runs before the insight/reputation passes so
        // junk items never earn a brand-reputation call. Product intent only — service/place results
        // are provider/venue lists where the product-type question doesn't apply. Best-effort: any
        // failure keeps the full list.
        if (useLlmExtraction && intent == SearchIntentType.Product && models.Count > 1)
        {
            var target = intelligence?.Schema is { IsEmpty: false } sch ? sch.ProductType : query;
            models = await FilterRelevantModelsAsync(target, models, cancellationToken).ConfigureAwait(false);
        }

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

        // Wire the result-level research onto EACH model's own card: offers link to their selling
        // store's site, models gain the brand's regional site + their page on the brand site, and
        // every review/web hit that names the model is attached as an item review/mention. Runs
        // after the stores dictionary is complete (brand-page fallbacks + Places stores included).
        models = AttachItemResearch(models, bundle.WebResults, stores.Values, brands.Values, reviews, geo);

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

    /// <summary>Max item reviews attached to one model's card.</summary>
    private const int MaxItemReviews = 5;

    /// <summary>Max mention links attached to one model's card.</summary>
    private const int MaxItemMentions = 5;

    /// <summary>
    /// Max models one review may attach to — a review naming more than this many models is a
    /// generic roundup, not a review OF any one of them.
    /// </summary>
    private const int MaxModelsPerReview = 3;

    /// <summary>Shared significant (3+ char) tokens required to call a review/hit "about this model".</summary>
    private const int MinSharedIdentityTokens = 3;

    /// <summary>Shared name tokens required for a brand-site hit when the model number is unknown.</summary>
    private const int MinSharedNameTokens = 2;

    /// <summary>
    /// Wires the result-level research (stores/brand pages/review articles/raw web hits) onto EACH
    /// <see cref="ProductModel"/>, per the item-card contract:
    /// <list type="bullet">
    ///   <item>Offers get <see cref="PriceOffer.StoreUrl"/> — the selling store's own site — matched
    ///   from the stores dictionary by name (offer source) or by URL host.</item>
    ///   <item><see cref="ProductModel.BrandRegionalUrl"/> — the brand's site for the market, from a
    ///   classified brand page matching the model's brand (a local-looking URL preferred, the global
    ///   site kept as a fallback: better than nothing).</item>
    ///   <item><see cref="ProductModel.BrandSiteUrl"/> — the MODEL's page on the brand's own domain:
    ///   a web hit whose registrable host label carries the brand AND whose text names the model.</item>
    ///   <item><see cref="ProductModel.Reviews"/> / <see cref="ProductModel.Mentions"/> — review
    ///   articles and remaining web hits (reddit threads included — non-commerce is exactly where
    ///   mentions live) that name the model, token-matched and capped per model.</item>
    /// </list>
    /// Token sets are computed ONCE per model and once per review/result before the double loops —
    /// grids reach 50+ models × 350 results, so the association must never re-tokenize inside them.
    /// </summary>
    private static IReadOnlyList<ProductModel> AttachItemResearch(
        IReadOnlyList<ProductModel> models,
        IReadOnlyList<SearchResult> webResults,
        IEnumerable<StoreInfo> stores,
        IEnumerable<BrandInfo> brandPages,
        IReadOnlyList<ReviewSource> reviews,
        GeoProfile geo)
    {
        if (models.Count == 0)
        {
            return models;
        }

        // Store lookups for the offer → store-site link: by display name and by URL host.
        var storeUrlByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var storeUrlByHost = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in stores)
        {
            if (string.IsNullOrWhiteSpace(s.Url))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(s.Name))
            {
                storeUrlByName.TryAdd(s.Name, s.Url!);
            }

            if (HostOf(s.Url) is { } host)
            {
                storeUrlByHost.TryAdd(host, s.Url!);
            }
        }

        var brandCandidates = brandPages.Where(b => !string.IsNullOrWhiteSpace(b.Url)).ToList();

        // Tokenize each side ONCE; the model × review/result loops below only intersect sets.
        var reviewData = reviews
            .Where(r => !string.IsNullOrWhiteSpace(r.Url))
            .Select(r => (Review: r, Tokens: ItemTokens($"{r.Title} {r.Snippet}")))
            .ToList();
        var reviewUses = new int[reviewData.Count];

        var resultData = webResults
            .Where(r => !string.IsNullOrWhiteSpace(r.Url))
            .Select(r => (
                Result: r,
                Tokens: ItemTokens($"{r.Title} {r.Snippet} {UrlPath(r.Url)}"),
                HostBrand: NormalizeLabel(HostLabel(r.Url)),
                UrlShapedTitle: LooksLikeUrl(r.Title)))
            .ToList();

        var updated = new List<ProductModel>(models.Count);
        foreach (var m in models)
        {
            var identityTokens = ItemTokens($"{m.Brand} {m.Model} {m.Name}");
            var modelTokens = ItemTokens(m.Model);
            var nameTokens = ItemTokens(m.Name);
            var normalizedBrand = NormalizeLabel(m.Brand);

            // "About this model": the model number's tokens all present (the strongest signal), or
            // enough of the item's identity shared that a lone incidental word can never attach.
            bool MentionsModel(HashSet<string> candidate) =>
                (modelTokens.Count > 0 && modelTokens.All(candidate.Contains)) ||
                SharedSignificantTokens(identityTokens, candidate) >= MinSharedIdentityTokens;

            // 1) StoreUrl on offers: the seller chip links to the store's own site, distinct from
            // the offer's product-page Url (never duplicated when they're the same link).
            var offers = m.Offers;
            List<PriceOffer>? patched = null;
            for (var i = 0; i < offers.Count; i++)
            {
                var o = offers[i];
                if (o.StoreUrl is not null)
                {
                    continue;
                }

                string? storeUrl = null;
                if (!string.IsNullOrWhiteSpace(o.Source))
                {
                    storeUrlByName.TryGetValue(o.Source, out storeUrl);
                }

                if (storeUrl is null && HostOf(o.Url) is { } offerHost)
                {
                    storeUrlByHost.TryGetValue(offerHost, out storeUrl);
                }

                if (storeUrl is null || storeUrl.Equals(o.Url, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                patched ??= offers.ToList();
                patched[i] = o with { StoreUrl = storeUrl };
            }

            if (patched is not null)
            {
                offers = patched;
            }

            // 2) BrandRegionalUrl: prefer a brand page confirmed local to the market (or carrying
            // the /{cc} path), else fall back to the brand's global site — better than nothing.
            var brandRegionalUrl = m.BrandRegionalUrl;
            if (brandRegionalUrl is null && !string.IsNullOrWhiteSpace(m.Brand))
            {
                string? globalUrl = null;
                foreach (var bp in brandCandidates)
                {
                    if (!NamesMatch(bp.Name, m.Brand!))
                    {
                        continue;
                    }

                    if (LocalityClassifier.IsLocal(bp.Url, geo.CountryCode, geo.Country,
                            fromGeoScopedSearch: true, marketEvidence: $"{bp.Name} {geo.Country}") ||
                        bp.Url!.Contains($"/{geo.CountryCode}", StringComparison.OrdinalIgnoreCase))
                    {
                        brandRegionalUrl = bp.Url;
                        break;
                    }

                    globalUrl ??= bp.Url;
                }

                brandRegionalUrl ??= globalUrl;
            }

            // 3) BrandSiteUrl: the model's own page on the brand's domain — the host's registrable
            // label must carry the brand, and the hit's text must name the model. First hit wins.
            var brandSiteUrl = m.BrandSiteUrl;
            if (brandSiteUrl is null && normalizedBrand.Length > 0)
            {
                foreach (var rd in resultData)
                {
                    if (rd.HostBrand.Length == 0 ||
                        !rd.HostBrand.Contains(normalizedBrand, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var namesModel = modelTokens.Count > 0
                        ? modelTokens.All(rd.Tokens.Contains)
                        : SharedTokens(nameTokens, rd.Tokens) >= MinSharedNameTokens;
                    if (namesModel)
                    {
                        brandSiteUrl = rd.Result.Url;
                        break;
                    }
                }
            }

            // 4) Reviews that name THIS model, capped per model AND per review (a review spread
            // over many models is a roundup, not a review of any one of them).
            var itemReviews = new List<ItemReview>();
            for (var i = 0; i < reviewData.Count && itemReviews.Count < MaxItemReviews; i++)
            {
                if (reviewUses[i] >= MaxModelsPerReview || !MentionsModel(reviewData[i].Tokens))
                {
                    continue;
                }

                reviewUses[i]++;
                var rv = reviewData[i].Review;
                itemReviews.Add(new ItemReview(rv.Title, rv.Url!, rv.Snippet, rv.Source));
            }

            // 5) Mentions: any remaining web hit naming the model — a reddit thread or blog post is
            // exactly what belongs here — skipping links already on the card and URL-shaped titles.
            var usedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in offers)
            {
                if (!string.IsNullOrWhiteSpace(o.Url))
                {
                    usedUrls.Add(o.Url!);
                }
            }

            foreach (var rv in itemReviews)
            {
                usedUrls.Add(rv.Url);
            }

            if (brandSiteUrl is not null)
            {
                usedUrls.Add(brandSiteUrl);
            }

            var mentions = new List<ItemLink>();
            foreach (var rd in resultData)
            {
                if (mentions.Count >= MaxItemMentions)
                {
                    break;
                }

                if (rd.UrlShapedTitle || string.IsNullOrWhiteSpace(rd.Result.Title) ||
                    usedUrls.Contains(rd.Result.Url!) || !MentionsModel(rd.Tokens))
                {
                    continue;
                }

                mentions.Add(new ItemLink(rd.Result.Title, rd.Result.Url!, rd.Result.Source));
            }

            updated.Add(m with
            {
                Offers = offers,
                Reviews = m.Reviews.Count > 0 ? m.Reviews : itemReviews,
                Mentions = m.Mentions.Count > 0 ? m.Mentions : mentions,
                BrandSiteUrl = brandSiteUrl,
                BrandRegionalUrl = brandRegionalUrl
            });
        }

        return updated;

        static bool NamesMatch(string a, string b) =>
            a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
            a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
            b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Separators for item-association tokenizing (mirrors the enrichment matcher's set).</summary>
    private static readonly char[] ItemTokenSeparators = " \t\r\n-_/\\|,.()[]{}،:;\"'".ToCharArray();

    /// <summary>
    /// Token set for item association. Keeps 2-character tokens: variant suffixes ("FE", "5G", or a
    /// hyphen-split model half like "S4") are exactly what distinguishes one model from another.
    /// </summary>
    private static HashSet<string> ItemTokens(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return set;
        }

        foreach (var t in text.Split(ItemTokenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (t.Length >= 2)
            {
                set.Add(t);
            }
        }

        return set;
    }

    /// <summary>Count of tokens present in both sets.</summary>
    private static int SharedTokens(HashSet<string> want, HashSet<string> have)
    {
        var shared = 0;
        foreach (var t in want)
        {
            if (have.Contains(t))
            {
                shared++;
            }
        }

        return shared;
    }

    /// <summary>Shared-token count over SIGNIFICANT (3+ char) tokens, so "of"/"in" never count.</summary>
    private static int SharedSignificantTokens(HashSet<string> want, HashSet<string> have)
    {
        var shared = 0;
        foreach (var t in want)
        {
            if (t.Length >= 3 && have.Contains(t))
            {
                shared++;
            }
        }

        return shared;
    }

    /// <summary>Lowercased alphanumerics of a label ("Smart Buy" → "smartbuy") for host↔brand matching.</summary>
    private static string NormalizeLabel(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : new string(text!.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Lowercased URL host without a "www." prefix, or null when unparseable.</summary>
    private static string? HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return null;
        }

        var host = u.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>A URL's path component, for token matching ("/jo/air-conditioners/ar18" names a model).</summary>
    private static string UrlPath(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : string.Empty;

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
            .Where(c => includeIntl || LocalityClassifier.IsLocal(
                c.r.Url, geo.CountryCode, geo.Country,
                fromGeoScopedSearch: !string.IsNullOrWhiteSpace(geo.CountryCode),
                marketEvidence: $"{c.r.Title} {c.r.Snippet}"))
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
        ProductSchema? schema = null, SearchIntentType intent = SearchIntentType.Product)
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
        var dto = await ExtractProductsDtoAsync(query, geo, context, cancellationToken, schema, intent).ConfigureAwait(false);
        if (dto?.Products is null)
        {
            return empty;
        }

        var listings = new List<ProductListing>();
        var insights = new Dictionary<string, ModelInsight>(StringComparer.Ordinal);
        foreach (var p in dto.Products)
        {
            var name = PickName(p.Name, p.Model);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue; // nothing to identify the product by (or the LLM only gave a URL)
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
                            LocalityClassifier.IsLocal(
                                t.url, geo.CountryCode, geo.Country,
                                fromGeoScopedSearch: !string.IsNullOrWhiteSpace(geo.CountryCode)))
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

    // A URL / bare domain the LLM sometimes drops into the name field instead of the product name:
    // "https://x.com/p", "www.foo.io", "amazon.com/dp/B0…". The final label must be all letters (a TLD)
    // so real model numbers like "GX-3.5" or "A2.1" are NOT mistaken for domains.
    private static readonly Regex UrlLikeName = new(
        @"^\s*(https?://|www\.)|^\s*([a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,24}(/\S*)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Chooses the product's display name, guarding against the LLM occasionally emitting the source URL
    /// (or a bare domain) in the name field instead of the real product name. A URL-shaped name is rejected
    /// in favour of the model number; if that is also missing or URL-shaped, the product is dropped rather
    /// than surfacing a raw link to the shopper.
    /// </summary>
    private static string? PickName(string? name, string? model)
    {
        if (!string.IsNullOrWhiteSpace(name) && !LooksLikeUrl(name)) return name;
        if (!string.IsNullOrWhiteSpace(model) && !LooksLikeUrl(model)) return model;
        return null;
    }

    private static bool LooksLikeUrl(string text) => UrlLikeName.IsMatch(text.Trim());

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
        string query, GeoProfile geo, string context, CancellationToken cancellationToken,
        ProductSchema? schema = null, SearchIntentType intent = SearchIntentType.Product)
    {
        // Both the user prompt and the system prompt switch on intent: a product is parsed for prices and
        // specs, a service for pricing tiers and contact, a place for hours/address/map — all into the same
        // JSON shape so the rest of this method is intent-agnostic.
        var prompt = PromptTemplates.ExtractProducts(query, geo, context, schema, intent);
        var system = PromptTemplates.ExtractionSystem(intent);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var text = await _llm.CompleteTextAsync(
                    system, prompt, cancellationToken).ConfigureAwait(false);

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
    /// Runs the LLM relevance gate over the aggregated models: one call, one question per item —
    /// "is this item ITSELF a &lt;target&gt;?" — and drops the items that aren't (accessories,
    /// consumables, different product types, haram content). Best-effort by design: a failed call,
    /// unparseable reply, or an implausible drop-everything verdict keeps the original list, so this
    /// gate can reduce noise but can never empty a result or fault a search.
    /// </summary>
    private async Task<IReadOnlyList<ProductModel>> FilterRelevantModelsAsync(
        string target, IReadOnlyList<ProductModel> models, CancellationToken cancellationToken)
    {
        try
        {
            var labels = models
                .Select(m => string.Join(" — ", new[] { m.Name, m.Brand, m.Model }
                    .Where(s => !string.IsNullOrWhiteSpace(s))))
                .ToList();

            var text = await _llm.CompleteTextAsync(
                PromptTemplates.RelevanceGateSystem,
                PromptTemplates.RelevanceGate(target, labels),
                cancellationToken).ConfigureAwait(false);

            var dto = LlmJson.Deserialize<RelevanceVerdictsDto>(text);
            if (dto?.Drop is not { Count: > 0 } drop)
            {
                return models; // nothing flagged (or unparseable reply) — keep everything
            }

            var kept = ApplyRelevanceVerdicts(models, drop);
            if (kept.Count == models.Count)
            {
                return models;
            }

            // Sanity guard: a verdict that wipes (nearly) the whole grid is far more likely a gate
            // misfire — or a prompt-injected product name — than 80%+ genuine noise. Distrust it and
            // keep the originals; a noisy grid beats the empty-results failure class.
            if (kept.Count < Math.Max(1, models.Count / 5))
            {
                Log($"relevance gate flagged {models.Count - kept.Count}/{models.Count} items — implausible, ignoring the verdict");
                return models;
            }

            Log($"🧹 Relevance gate removed {models.Count - kept.Count} item(s) that aren't a {target}.");
            return kept;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"relevance gate failed: {ex.Message}");
            return models; // best-effort — never fault the search over a relevance pass
        }
    }

    /// <summary>
    /// Applies a drop-index verdict to the model list: listed indices are removed, out-of-range or
    /// duplicate indices are ignored, unlisted items are kept (the gate fails open per item).
    /// Pure and static so the verdict semantics are unit-testable without an LLM.
    /// </summary>
    internal static IReadOnlyList<ProductModel> ApplyRelevanceVerdicts(
        IReadOnlyList<ProductModel> models, IReadOnlyList<int> dropIndices)
    {
        var drop = new HashSet<int>(dropIndices.Where(i => i >= 0 && i < models.Count));
        if (drop.Count == 0)
        {
            return models;
        }

        return models.Where((_, i) => !drop.Contains(i)).ToList();
    }

    /// <summary>Wire shape for the relevance-gate LLM output.</summary>
    private sealed class RelevanceVerdictsDto
    {
        [JsonPropertyName("drop")] public List<int>? Drop { get; set; }
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
            // Price, currency, URL and the indicative flag all come from the SAME offer — mixing
            // LowestPrice (min over ALL offers incl. indicative) with another offer's currency/link
            // minted figures that existed on no offer and dropped the ≈ semantics downstream.
            Price = offer?.Price,
            IsIndicative = offer?.IsIndicative ?? false,
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
        // A price lifted from a web result's SNIPPET is a potential price, not a quote — the page
        // may show a range, an old price, or a different variant. Mark it indicative so the UI
        // renders an ≈ "verify at the store" affordance instead of a firm figure.
        IsIndicative = r.Price is not null,
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

    /// <summary>
    /// Title-cased REGISTRABLE label of a host — the brand-carrying label immediately left of the
    /// public suffix, never a subdomain: "https://www.samsung.com/jo" → "Samsung",
    /// "jo.opensooq.com" → "Opensooq" (jo is the country subdomain, not the store),
    /// "khaleej.com.sa" → "Khaleej", "leaders.jo" → "Leaders". Taking the FIRST label minted
    /// country/language/mobile subdomains ("Jo", "M", "En") as store names.
    /// </summary>
    public static string? HostLabel(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return null;
        }

        var labels = u.Host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length == 0)
        {
            return null;
        }

        // Approximate public-suffix walk from the right: "<generic>.<cc>" pairs (com.jo, co.uk,
        // com.sa…) are two-label suffixes, anything else is one. The label before the suffix is the
        // registrable one — correct for any depth of subdomain without listing subdomains.
        var suffixLen = 1;
        if (labels.Length >= 3 && labels[^1].Length == 2 && GenericSecondLevels.Contains(labels[^2]))
        {
            suffixLen = 2;
        }

        var label = labels.Length > suffixLen ? labels[^(suffixLen + 1)] : labels[0];
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        return char.ToUpperInvariant(label[0]) + label[1..];
    }

    /// <summary>Generic second-level labels that pair with a country code to form a public suffix.</summary>
    private static readonly HashSet<string> GenericSecondLevels =
        new(StringComparer.OrdinalIgnoreCase) { "com", "co", "net", "org", "gov", "edu", "mil", "ac" };

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
