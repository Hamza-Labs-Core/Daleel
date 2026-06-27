using System.Text;
using System.Text.Json.Serialization;
using Daleel.Core.Analysis;
using Daleel.Core.Arabic;
using Daleel.Core.Geo;
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

    public AgentService(
        ILlmClient llm,
        AgentOptions? options = null,
        ISearchProvider? search = null,
        IPlacesProvider? places = null,
        IScrapeProvider? scraper = null,
        IPostFetcher? social = null,
        IPostMatcher? matcher = null,
        OpinionExtractor? opinions = null,
        ContentFilter? contentFilter = null)
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
        _filter = contentFilter ?? new ContentFilter(FilterStrictness.Strict);
    }

    /// <summary>The halal content filter applied to gathered results (audit log lives on it).</summary>
    public ContentFilter ContentFilter => _filter;

    // ── Planning ───────────────────────────────────────────────────────────────

    /// <summary>Asks the LLM to turn a planning prompt into a <see cref="SearchStrategy"/>.</summary>
    public async Task<SearchStrategy> PlanAsync(string planningPrompt, CancellationToken cancellationToken = default)
    {
        var text = await _llm.CompleteTextAsync(PromptTemplates.PlannerSystem, planningPrompt, cancellationToken)
            .ConfigureAwait(false);

        var dto = LlmJson.Deserialize<StrategyDto>(text);
        return dto?.ToStrategy() ?? new SearchStrategy { Reasoning = "Planner returned no usable JSON." };
    }

    // ── Top-level entry points ──────────────────────────────────────────────────

    /// <summary>Answers a free-form question: plan → gather → analyze.</summary>
    public async Task<AgentAnswer> AskAsync(string question, string? geoKey = null, CancellationToken cancellationToken = default)
    {
        var geo = GeoProfiles.ResolveOrDefault(geoKey ?? _options.DefaultGeo);
        Log($"Planning research for: {question} [{geo.Country}]");

        var strategy = await PlanAsync(PromptTemplates.PlanFreeform(question, geo), cancellationToken).ConfigureAwait(false);
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
        // Strip bars, liquor stores, and other non-halal venues before returning.
        var halal = _filter.FilterStores(stores);
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
        return await _llm.CompleteTextAsync(systemPrompt ?? PromptTemplates.AnalystSystem, prompt, cancellationToken)
            .ConfigureAwait(false);
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
                sb.Append("- ").Append(r.Title).Append(" — ").Append(r.Snippet);
                if (!string.IsNullOrWhiteSpace(r.Url)) sb.Append(" (").Append(r.Url).Append(')');
                // Surface the result's thumbnail so the extractor can attach a real image URL to the
                // product. Without this the LLM has no image to return — a common cause of missing
                // product images in the grid.
                if (!string.IsNullOrWhiteSpace(r.ImageUrl)) sb.Append(" [image: ").Append(r.ImageUrl).Append(']');
                sb.AppendLine();
            }
        }

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

        foreach (var page in bundle.Pages.Take(3))
        {
            sb.AppendLine($"# Page: {page.Url}");
            sb.AppendLine(Truncate(page.Content, 2000));
        }

        return sb.ToString();
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
        var total = _filter.AuditLog.Count;
        if (total > _lastFilteredCount)
        {
            Log($"🧹 Halal filter removed {total - _lastFilteredCount} non-compliant item(s).");
            _lastFilteredCount = total;
        }
    }

    /// <summary>Wire shape for the planner's JSON output.</summary>
    private sealed class StrategyDto
    {
        [JsonPropertyName("queryType")] public string? QueryTypeRaw { get; set; }
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
