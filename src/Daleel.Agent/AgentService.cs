using System.Text;
using System.Text.Json.Serialization;
using Daleel.Core.Analysis;
using Daleel.Core.Arabic;
using Daleel.Core.Geo;
using Daleel.Core.Intelligence;
using Daleel.Core.Llm;
using Daleel.Core.Models;
using Daleel.Core.Moderation;
using Daleel.Core.Pipeline;
using Daleel.Pipeline;
using Daleel.Search.Abstractions;
using Daleel.Search.Moderation;

namespace Daleel.Agent;

/// <summary>
/// The intelligence engine. The LLM is used twice — as the <em>planner</em> that turns a
/// query into a bilingual <see cref="SearchStrategy"/>, and as the <em>analyst</em> that
/// turns gathered results into a written report. Between those, concrete providers fetch
/// web/shopping/places/social data in parallel and the Arabic matcher filters social
/// noise.
/// </summary>
/// <remarks>
/// Every external collaborator is an injected interface and every provider call is
/// failure-isolated, so the agent degrades gracefully when a given provider is absent or
/// errors — and the whole class is unit-testable with fakes (fake LLM + fake search).
/// </remarks>
public sealed partial class AgentService
{
    private readonly ILlmClient _llm;
    private readonly AgentOptions _options;
    private readonly ISearchProvider? _search;
    private readonly IPlacesProvider? _places;
    private readonly IScrapeProvider? _scraper;
    private readonly IPostFetcher? _social;
    private readonly IPostMatcher _matcher;
    private readonly OpinionExtractor? _opinions;
    private readonly ContentFilter _filter;
    private readonly HalalModerator _moderator;

    public AgentService(
        ILlmClient llm,
        AgentOptions? options = null,
        ISearchProvider? search = null,
        IPlacesProvider? places = null,
        IScrapeProvider? scraper = null,
        IPostFetcher? social = null,
        IPostMatcher? matcher = null,
        OpinionExtractor? opinions = null,
        ContentFilter? contentFilter = null,
        HalalModerator? moderator = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _options = options ?? new AgentOptions();
        _search = search;
        _places = places;
        _scraper = scraper;
        _social = social;
        _matcher = matcher ?? new ArabicMatcher();
        _opinions = opinions;
        // Default to Strict so callers that don't supply a filter still get halal-compliant output.
        // A supplied moderator must carry the same filter instance (shared audit); when only a
        // filter is given the moderator degrades to the keyword-only pipeline over it.
        _filter = moderator?.Filter ?? contentFilter ?? new ContentFilter(FilterStrictness.Strict);
        _moderator = moderator ?? new HalalModerator(_filter);
    }

    /// <summary>The halal content filter applied to gathered results (audit log lives on it).</summary>
    public ContentFilter ContentFilter => _filter;

    // ── Planning ───────────────────────────────────────────────────────────────

    /// <summary>Asks the LLM to turn a planning prompt into a <see cref="SearchStrategy"/>.</summary>
    public async Task<SearchStrategy> PlanAsync(
        string planningPrompt, CancellationToken cancellationToken = default, string? userQuery = null)
    {
        string text;
        using (LlmCallSiteScope.Enter(LlmCallSites.Planner))
        {
            text = await _llm.CompleteTextAsync(PromptTemplates.PlannerSystem, planningPrompt, cancellationToken)
                .ConfigureAwait(false);
        }

        var dto = LlmJson.Deserialize<StrategyDto>(text);
        var strategy = dto?.ToStrategy() ?? new SearchStrategy { Reasoning = "Planner returned no usable JSON." };

        // Deterministic classification backstop: the LLM occasionally labels an obvious buy-intent
        // query ("best espresso machine") General, which silently skips the whole product pipeline
        // and renders an empty "No results just yet". When the caller supplies the raw user query,
        // an unmistakably buy-intent-shaped one is upgraded to ProductResearch.
        if (userQuery is not null &&
            BuyIntentHeuristic.Coerce(strategy.QueryType, userQuery) is var coerced &&
            coerced != strategy.QueryType)
        {
            Log("🧭 Buy-intent phrasing detected — treating this as product research.");
            strategy = strategy with { QueryType = coerced };
        }

        return strategy;
    }

    // ── Top-level entry points ──────────────────────────────────────────────────

    /// <summary>Answers a free-form question: plan → gather → analyze.</summary>
    public async Task<AgentAnswer> AskAsync(string question, string? geoKey = null, CancellationToken cancellationToken = default)
    {
        var geo = GeoProfiles.ResolveOrDefault(geoKey ?? _options.DefaultGeo);
        Log($"Planning research for: {question} [{geo.Country}]");

        var strategy = await PlanAsync(PromptTemplates.PlanFreeform(question, geo), cancellationToken, userQuery: question)
            .ConfigureAwait(false);
        var bundle = await GatherAsync(strategy, geo, cancellationToken).ConfigureAwait(false);

        var isProduct = strategy.QueryType == QueryType.ProductResearch;
        var summary = await AnalyzeAsync(question, geo, bundle, cancellationToken,
            isProduct ? PromptTemplates.ProductAnalystSystem : null).ConfigureAwait(false);

        // For product searches, also project the bundle into structured, link-rich listings
        // (reusing the bundle we already gathered — no extra planning/search round-trip).
        ProductSearchResult? products = null;
        if (isProduct)
        {
            products = await BuildProductSearchResultAsync(strategy.Subject is { Length: > 0 } s ? s : question,
                geo, bundle, summary, cancellationToken).ConfigureAwait(false);
        }

        return new AgentAnswer
        {
            Question = question,
            Geo = geo.Key,
            QueryType = strategy.QueryType,
            Summary = summary,
            Research = bundle,
            Products = products,
            GeneratedAt = _options.Clock()
        };
    }

    /// <summary>Produces a brand-intelligence report.</summary>
    public async Task<BrandReport> ResearchBrandAsync(string brand, string? geoKey = null, CancellationToken cancellationToken = default)
    {
        var geo = GeoProfiles.ResolveOrDefault(geoKey ?? _options.DefaultGeo);
        var strategy = await PlanAsync(PromptTemplates.PlanBrand(brand, geo), cancellationToken).ConfigureAwait(false);
        var bundle = await GatherAsync(strategy, geo, cancellationToken).ConfigureAwait(false);
        var summary = await AnalyzeAsync($"Brand intelligence for {brand}", geo, bundle, cancellationToken).ConfigureAwait(false);

        return new BrandReport
        {
            Brand = brand,
            Geo = geo.Key,
            Summary = summary,
            Stores = bundle.ShoppingResults.Select(ToStoreResult).ToList(),
            Locations = bundle.Stores,
            Sentiment = SentimentFromPosts(bundle.SocialPosts, brand),
            Sources = bundle.Sources,
            GeneratedAt = _options.Clock()
        };
    }

    /// <summary>Produces a product-category intelligence report.</summary>
    public async Task<ProductIntelligence> ResearchProductAsync(string category, string? geoKey = null, CancellationToken cancellationToken = default)
    {
        var geo = GeoProfiles.ResolveOrDefault(geoKey ?? _options.DefaultGeo);
        var strategy = await PlanAsync(PromptTemplates.PlanProduct(category, geo), cancellationToken).ConfigureAwait(false);
        var bundle = await GatherAsync(strategy, geo, cancellationToken).ConfigureAwait(false);
        var summary = await AnalyzeAsync($"Best {category} in {geo.Country}", geo, bundle, cancellationToken).ConfigureAwait(false);

        var opinions = await ExtractOpinionsAsync(category, bundle, cancellationToken).ConfigureAwait(false);

        return new ProductIntelligence
        {
            Category = category,
            Geo = geo.Key,
            Summary = summary,
            Prices = bundle.Prices,
            Opinions = opinions,
            Stores = bundle.Stores,
            Sources = bundle.Sources,
            GeneratedAt = _options.Clock()
        };
    }

    /// <summary>Finds stores selling a subject, optionally near a point.</summary>
    public async Task<IReadOnlyList<StoreLocation>> FindStoresAsync(
        string subject, string? geoKey = null, GeoPoint? near = null, CancellationToken cancellationToken = default)
    {
        var geo = GeoProfiles.ResolveOrDefault(geoKey ?? _options.DefaultGeo);
        if (_places is null)
        {
            return Array.Empty<StoreLocation>();
        }

        var strategy = await PlanAsync(PromptTemplates.PlanStores(subject, geo), cancellationToken).ConfigureAwait(false);
        var point = near ?? geo.Center;

        var queries = strategy.PlacesQueries.Count > 0 ? strategy.PlacesQueries : new[] { subject };
        var stores = await RunPlacesAsync(queries, geo, point, cancellationToken).ConfigureAwait(false);
        // Strip bars, liquor stores, and other non-halal venues before returning — item-granular,
        // with LLM adjudication of keyword flags when a classifier is configured.
        var halal = await _moderator.ModerateAsync(stores, ModerationProjections.Store, cancellationToken)
            .ConfigureAwait(false);
        LogFilteredCount();
        return halal;
    }

    /// <summary>Compares two or more products head-to-head.</summary>
    public async Task<ProductComparison> CompareAsync(
        IReadOnlyList<string> products, string? geoKey = null, CancellationToken cancellationToken = default)
    {
        var geo = GeoProfiles.ResolveOrDefault(geoKey ?? _options.DefaultGeo);
        var joined = string.Join(" vs ", products);
        var strategy = await PlanAsync(PromptTemplates.PlanProduct(joined, geo), cancellationToken).ConfigureAwait(false);
        var bundle = await GatherAsync(strategy, geo, cancellationToken).ConfigureAwait(false);
        var summary = await AnalyzeAsync($"Compare: {joined}", geo, bundle, cancellationToken).ConfigureAwait(false);

        // Determine the product type up-front so the compare page can show WHICH dimensions decide
        // this category (BTU/energy for ACs, RAM/storage for phones). Uses the planner's understood
        // subject when it has one, else the joined product names.
        var category = strategy.Subject is { Length: > 0 } s ? s : joined;
        var intelligence = await AnalyzeCategoryAsync(category, geo, cancellationToken).ConfigureAwait(false);

        return new ProductComparison
        {
            Products = products,
            Geo = geo.Key,
            Summary = summary,
            Schema = intelligence.Schema,
            Sources = bundle.Sources,
            GeneratedAt = _options.Clock()
        };
    }

    // ── Analyze ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The analyst pass: flattens the gathered bundle into context and asks the LLM for a written
    /// summary. Public so the Elsa search workflow can drive it as a discrete activity (the
    /// orchestration <see cref="AskAsync"/> performs inline).
    /// </summary>
    public async Task<string> AnalyzeAsync(
        string task, GeoProfile geo, ResearchBundle bundle, CancellationToken cancellationToken,
        string? systemPrompt = null)
    {
        var context = BuildContext(bundle);
        if (context.Length == 0)
        {
            return "No research data was gathered (no search providers configured or all returned empty).";
        }

        var prompt = PromptTemplates.Analyze(task, geo, context, _options.Language);
        using (LlmCallSiteScope.Enter(LlmCallSites.Analyst))
        {
            return await _llm.CompleteTextAsync(systemPrompt ?? PromptTemplates.AnalystSystem, prompt, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A raw metered completion for an arbitrary system+user prompt — the entry point the enrichment
    /// synthesis units use to "make sense of" a settled result. It runs on the SAME <c>_llm</c> this
    /// service was built with, which is the metered/credit-charged/cost-capped wrapper whenever the
    /// service was built via AgentFactory with an ApiObserver (the enrichment consumer always does),
    /// so this call is billed exactly like every other pipeline LLM call. Unlike
    /// <see cref="AnalyzeAsync"/> it prepends no analyst context and returns the model's text verbatim.
    /// </summary>
    public async Task<string> SynthesizeAsync(
        string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        using (LlmCallSiteScope.Enter(LlmCallSites.Synthesis))
        {
            return await _llm.CompleteTextAsync(systemPrompt, userPrompt, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<CustomerOpinion>> ExtractOpinionsAsync(
        string subject, ResearchBundle bundle, CancellationToken cancellationToken)
    {
        if (_opinions is null)
        {
            return Array.Empty<CustomerOpinion>();
        }

        var texts = bundle.SocialPosts.Select(p => p.Text)
            .Concat(bundle.Pages.Select(p => p.Content))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Take(40)
            .ToList();

        if (texts.Count == 0)
        {
            return Array.Empty<CustomerOpinion>();
        }

        return await _opinions.ExtractAsync(subject, texts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Flattens a bundle into a compact text block for the analyst prompt.</summary>
    private static string BuildContext(ResearchBundle bundle)
    {
        var sb = new StringBuilder();

        if (bundle.WebResults.Count > 0)
        {
            sb.AppendLine("# Web results");
            foreach (var r in bundle.WebResults.Take(20))
            {
                AppendWebResult(sb, r);
            }
        }

        AppendNonWebContext(sb, bundle);
        return sb.ToString();
    }

    /// <summary>One bounded unit of extraction input — a labelled context chunk that gets its own LLM call.</summary>
    internal readonly record struct ExtractionPart(string Label, string Content);

    /// <summary>
    /// Splits a bundle into bounded extraction PARTS for parallel per-part LLM extraction: one "signals"
    /// part (web-result classification into buyable listings vs reference-only editorial + the
    /// shopping/stores/social digest — all short) plus one part PER scraped page carrying its FULL content
    /// (capped at <paramref name="maxPageChars"/> so a single huge page can't reintroduce the monolithic
    /// hang). Each part becomes its own extraction call, so the uncapped "extract EVERY model" instruction
    /// sees a bounded input per call and completes — instead of one monolithic call over everything, which
    /// hung past the deadline (measured: 80 mixed inputs → stuck &gt;10 min).
    /// </summary>
    private static IReadOnlyList<ExtractionPart> BuildExtractionParts(ResearchBundle bundle, int maxPageChars)
    {
        var parts = new List<ExtractionPart>();

        // (1) The "signals" part: web-result classification + the non-web digest (shopping/stores/social),
        // WITHOUT the scraped pages — those become their own parts below.
        var signals = new StringBuilder();
        if (bundle.WebResults.Count > 0)
        {
            var classified = bundle.WebResults
                .Take(30)
                .Select(r => (r, type: ResultClassifier.Classify(r.Url, r.Title, r.Snippet)))
                .ToList();

            var listings = classified.Where(c => c.type != ResultType.ReviewArticle).Select(c => c.r).ToList();
            var articles = classified.Where(c => c.type == ResultType.ReviewArticle).Select(c => c.r).ToList();

            if (listings.Count > 0)
            {
                signals.AppendLine("# Product & store listings (real items/sellers — extract products from these)");
                foreach (var r in listings)
                {
                    AppendWebResult(signals, r);
                }
            }

            if (articles.Count > 0)
            {
                signals.AppendLine("# Reference articles (background SOURCES only — mine for which products/brands exist; " +
                    "NEVER list an article, review or round-up itself as a product)");
                foreach (var r in articles)
                {
                    AppendWebResult(signals, r);
                }
            }
        }

        AppendNonWebContext(signals, bundle, includePages: false);
        if (signals.Length > 0)
        {
            parts.Add(new ExtractionPart("signals", signals.ToString()));
        }

        // (2) One part per scraped page — FULL content (capped), NO 3-page limit. Store listing pages carry
        // the product density, so each is its own bounded extraction call.
        foreach (var page in bundle.Pages)
        {
            if (string.IsNullOrWhiteSpace(page.Content))
            {
                continue;
            }

            var content = page.Content.Length > maxPageChars ? page.Content[..maxPageChars] + "…" : page.Content;
            parts.Add(new ExtractionPart($"page:{page.Url}", $"# Page: {page.Url}\n{content}"));
        }

        return parts;
    }

    /// <summary>Appends one web result as a context bullet (title — snippet (url) [image]).</summary>
    private static void AppendWebResult(StringBuilder sb, SearchResult r)
    {
        sb.Append("- ").Append(r.Title).Append(" — ").Append(r.Snippet);
        if (!string.IsNullOrWhiteSpace(r.Url)) sb.Append(" (").Append(r.Url).Append(')');
        // Surface the result's thumbnail so the extractor can attach a real image URL to the
        // product. Without this the LLM has no image to return — a common cause of missing
        // product images in the grid.
        if (!string.IsNullOrWhiteSpace(r.ImageUrl)) sb.Append(" [image: ").Append(r.ImageUrl).Append(']');
        sb.AppendLine();
    }

    /// <summary>Appends the non-web sections (shopping, stores, social, and — when <paramref name="includePages"/>
    /// — scraped pages) shared by both context builders. Extraction passes includePages:false because each
    /// scraped page becomes its own extraction part.</summary>
    private static void AppendNonWebContext(StringBuilder sb, ResearchBundle bundle, bool includePages = true)
    {
        if (bundle.ShoppingResults.Count > 0)
        {
            sb.AppendLine("# Shopping results");
            foreach (var r in bundle.ShoppingResults.Take(20))
            {
                sb.Append("- ").Append(r.Title);
                if (r.Price is { } price) sb.Append(" — ").Append(price.ToDisplay());
                if (!string.IsNullOrWhiteSpace(r.Seller)) sb.Append(" @ ").Append(r.Seller);
                if (!string.IsNullOrWhiteSpace(r.Url)) sb.Append(" (").Append(r.Url).Append(')');
                if (!string.IsNullOrWhiteSpace(r.ImageUrl)) sb.Append(" [image: ").Append(r.ImageUrl).Append(']');
                sb.AppendLine();
            }
        }

        if (bundle.Stores.Count > 0)
        {
            sb.AppendLine("# Stores (Google Places)");
            foreach (var s in bundle.Stores.Take(15))
            {
                sb.Append("- ").Append(s.Name);
                if (s.Rating is { } rating) sb.Append(" (").Append(rating).Append("★, ").Append(s.ReviewCount).Append(" reviews)");
                if (!string.IsNullOrWhiteSpace(s.Address)) sb.Append(" — ").Append(s.Address);
                if (s.DistanceMeters is { } d) sb.Append(" [").Append(Math.Round(d / 1000, 1)).Append(" km]");
                sb.AppendLine();
            }
        }

        if (bundle.SocialPosts.Count > 0)
        {
            sb.AppendLine("# Social posts");
            foreach (var p in bundle.SocialPosts.Take(25))
            {
                sb.Append("- ").AppendLine(Truncate(p.Text, 280));
            }
        }

        if (includePages)
        {
            foreach (var page in bundle.Pages.Take(3))
            {
                sb.AppendLine($"# Page: {page.Url}");
                sb.AppendLine(Truncate(page.Content, 2000));
            }
        }
    }

    private static SentimentSummary SentimentFromPosts(IReadOnlyList<SocialPost> posts, string subject)
    {
        // Lightweight keyword sentiment as a fallback when no LLM opinion pass runs; the
        // analyst summary remains the authoritative narrative.
        var sentiments = posts.Select(p => KeywordSentiment.Score(p.Text));
        return SentimentSummary.FromOpinions(sentiments);
    }

    private static StoreResult ToStoreResult(SearchResult r) => new()
    {
        Name = r.Seller ?? r.Title,
        IsOnline = true,
        Url = r.Url,
        Price = r.Price,
        Source = r.Source
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private void Log(string message) => _options.Log?.Invoke(message);

    /// <summary>Surfaces how many items the halal filter removed (audited, never the items themselves).</summary>
    private int _lastFilteredCount;
    private void LogFilteredCount()
    {
        // This AgentService instance is shared by reference across parallel sub-workflows, so the
        // running total is advanced atomically — otherwise two threads could log the same delta twice
        // or miss a delta entirely. Only the thread that wins the bump logs its slice.
        var total = _filter.AuditLog.Count;
        int previous, updated;
        do
        {
            previous = Volatile.Read(ref _lastFilteredCount);
            if (total <= previous)
            {
                return;
            }
            updated = total;
        }
        while (Interlocked.CompareExchange(ref _lastFilteredCount, updated, previous) != previous);

        Log($"🧹 Halal filter removed {total - previous} non-compliant item(s).");
    }

    /// <summary>Wire shape for the planner's JSON output.</summary>
    private sealed class StrategyDto
    {
        [JsonPropertyName("queryType")] public string? QueryTypeRaw { get; set; }
        [JsonPropertyName("intent")] public string? IntentRaw { get; set; }
        [JsonPropertyName("subject")] public string? Subject { get; set; }
        [JsonPropertyName("webQueries")] public List<string>? WebQueries { get; set; }
        [JsonPropertyName("shoppingQueries")] public List<string>? ShoppingQueries { get; set; }
        [JsonPropertyName("socialQueries")] public List<string>? SocialQueries { get; set; }
        [JsonPropertyName("placesQueries")] public List<string>? PlacesQueries { get; set; }
        [JsonPropertyName("urlsToRead")] public List<string>? UrlsToRead { get; set; }
        [JsonPropertyName("reasoning")] public string? Reasoning { get; set; }

        public SearchStrategy ToStrategy() => new()
        {
            QueryType = Enum.TryParse<QueryType>(QueryTypeRaw, ignoreCase: true, out var qt) ? qt : Core.Models.QueryType.General,
            // Default to Product when the planner omits or garbles the intent, so the pipeline behaves
            // exactly as it did before intent classification existed.
            Intent = Enum.TryParse<SearchIntentType>(IntentRaw, ignoreCase: true, out var it) ? it : SearchIntentType.Product,
            Subject = Subject ?? string.Empty,
            WebQueries = WebQueries ?? new List<string>(),
            ShoppingQueries = ShoppingQueries ?? new List<string>(),
            SocialQueries = SocialQueries ?? new List<string>(),
            PlacesQueries = PlacesQueries ?? new List<string>(),
            UrlsToRead = UrlsToRead ?? new List<string>(),
            Reasoning = Reasoning
        };
    }
}
