using System.Text;
using System.Text.Json.Serialization;
using Daleel.Core.Arabic;
using Daleel.Core.Geo;
using Daleel.Core.Llm;
using Daleel.Core.Models;
using Daleel.Core.Pipeline;
using Daleel.Pipeline;
using Daleel.Search.Abstractions;

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
public sealed class AgentService
{
    private readonly ILlmClient _llm;
    private readonly AgentOptions _options;
    private readonly ISearchProvider? _search;
    private readonly IPlacesProvider? _places;
    private readonly IScrapeProvider? _scraper;
    private readonly IPostFetcher? _social;
    private readonly IPostMatcher _matcher;
    private readonly OpinionExtractor? _opinions;

    public AgentService(
        ILlmClient llm,
        AgentOptions? options = null,
        ISearchProvider? search = null,
        IPlacesProvider? places = null,
        IScrapeProvider? scraper = null,
        IPostFetcher? social = null,
        IPostMatcher? matcher = null,
        OpinionExtractor? opinions = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _options = options ?? new AgentOptions();
        _search = search;
        _places = places;
        _scraper = scraper;
        _social = social;
        _matcher = matcher ?? new ArabicMatcher();
        _opinions = opinions;
    }

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

        var summary = await AnalyzeAsync(question, geo, bundle, cancellationToken).ConfigureAwait(false);

        return new AgentAnswer
        {
            Question = question,
            Geo = geo.Key,
            QueryType = strategy.QueryType,
            Summary = summary,
            Research = bundle,
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
        return stores;
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

        return new ProductComparison
        {
            Products = products,
            Geo = geo.Key,
            Summary = summary,
            Sources = bundle.Sources,
            GeneratedAt = _options.Clock()
        };
    }

    // ── Gather ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes a strategy across all configured providers in parallel, normalizes the
    /// social posts with the Arabic matcher, and bundles the results.
    /// </summary>
    public async Task<ResearchBundle> GatherAsync(
        SearchStrategy strategy, GeoProfile geo, CancellationToken cancellationToken = default)
    {
        var webTask = RunSearchAsync(strategy.WebQueries, SearchKind.Web, geo, cancellationToken);
        var shopTask = RunSearchAsync(strategy.ShoppingQueries, SearchKind.Shopping, geo, cancellationToken);
        var placesTask = RunPlacesAsync(strategy.PlacesQueries, geo, geo.Center, cancellationToken);
        var socialTask = RunSocialAsync(strategy, geo, cancellationToken);
        var readTask = RunReadAsync(strategy.UrlsToRead, cancellationToken);

        await Task.WhenAll(webTask, shopTask, placesTask, socialTask, readTask).ConfigureAwait(false);

        var web = webTask.Result;
        var shopping = shopTask.Result;
        var stores = placesTask.Result;
        var social = socialTask.Result;
        var pages = readTask.Result;

        var prices = shopping
            .Where(r => r.Price is not null)
            .Select(r => new PricePoint
            {
                Product = r.Title,
                Price = r.Price!.Value,
                Store = r.Seller,
                Url = r.Url,
                ObservedAt = _options.Clock()
            })
            .ToList();

        var sources = web.Select(r => r.Url)
            .Concat(shopping.Select(r => r.Url))
            .Concat(pages.Select(p => p.Url))
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!)
            .Distinct()
            .ToList();

        return new ResearchBundle
        {
            Strategy = strategy,
            WebResults = web,
            ShoppingResults = shopping,
            Stores = stores,
            SocialPosts = social,
            Prices = prices,
            Pages = pages,
            Sources = sources
        };
    }

    private async Task<IReadOnlyList<SearchResult>> RunSearchAsync(
        IReadOnlyList<string> queries, SearchKind kind, GeoProfile geo, CancellationToken cancellationToken)
    {
        if (_search is null || !_search.Supports(kind) || queries.Count == 0)
        {
            return Array.Empty<SearchResult>();
        }

        var selected = queries.Take(_options.MaxQueriesPerKind).ToList();
        var tasks = selected.Select(q => SafeSearchAsync(new SearchQuery
        {
            Query = q,
            Kind = kind,
            CountryCode = geo.CountryCode,
            LanguageCode = geo.PrimaryLanguage,
            Location = geo.CenterCity,
            MaxResults = _options.ResultsPerQuery
        }, cancellationToken));

        var batches = await Task.WhenAll(tasks).ConfigureAwait(false);
        return batches.SelectMany(b => b).ToList();
    }

    private async Task<IReadOnlyList<SearchResult>> SafeSearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var results = await _search!.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            return results.Results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"search failed for '{query.Query}': {ex.Message}");
            return Array.Empty<SearchResult>();
        }
    }

    private async Task<IReadOnlyList<StoreLocation>> RunPlacesAsync(
        IReadOnlyList<string> queries, GeoProfile geo, GeoPoint near, CancellationToken cancellationToken)
    {
        if (_places is null || queries.Count == 0)
        {
            return Array.Empty<StoreLocation>();
        }

        var selected = queries.Take(_options.MaxQueriesPerKind).ToList();
        var all = new List<StoreLocation>();
        foreach (var q in selected)
        {
            try
            {
                var stores = await _places.SearchStoresAsync(q, near, 8000, geo.PrimaryLanguage, cancellationToken)
                    .ConfigureAwait(false);
                all.AddRange(stores);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"places failed for '{q}': {ex.Message}");
            }
        }

        // Dedupe by place id, keep closest-first.
        return all
            .GroupBy(s => s.PlaceId)
            .Select(g => g.First())
            .OrderBy(s => s.DistanceMeters ?? double.MaxValue)
            .ToList();
    }

    private async Task<IReadOnlyList<SocialPost>> RunSocialAsync(
        SearchStrategy strategy, GeoProfile geo, CancellationToken cancellationToken)
    {
        if (_social is null || strategy.SocialQueries.Count == 0)
        {
            return Array.Empty<SocialPost>();
        }

        var keyword = strategy.SocialQueries[0];
        var actor = geo.ApifyActors.FirstOrDefault();
        var source = new Source
        {
            Name = $"{geo.Key}-social",
            Kind = SourceKind.Search,
            Target = keyword,
            ActorId = actor,
            MaxItems = _options.ResultsPerQuery
        };

        try
        {
            var posts = await _social.FetchAsync(source, keyword, cancellationToken).ConfigureAwait(false);

            // Keep only posts that actually match one of the social keywords (Arabic-aware).
            return posts
                .Where(p => _matcher.Match(p.Text, strategy.SocialQueries, MatchMode.Contains).IsMatch)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"social fetch failed: {ex.Message}");
            return Array.Empty<SocialPost>();
        }
    }

    private async Task<IReadOnlyList<ScrapedPage>> RunReadAsync(
        IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        if (_scraper is null || urls.Count == 0)
        {
            return Array.Empty<ScrapedPage>();
        }

        var selected = urls.Take(_options.MaxUrlsToRead).ToList();
        var tasks = selected.Select(async url =>
        {
            try
            {
                return await _scraper.ScrapeAsync(url, ScrapeFormat.Markdown, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"scrape failed for '{url}': {ex.Message}");
                return new ScrapedPage { Url = url, Success = false, Error = ex.Message };
            }
        });

        var pages = await Task.WhenAll(tasks).ConfigureAwait(false);
        return pages.Where(p => p.Success).ToList();
    }

    // ── Analyze ─────────────────────────────────────────────────────────────────

    private async Task<string> AnalyzeAsync(
        string task, GeoProfile geo, ResearchBundle bundle, CancellationToken cancellationToken)
    {
        var context = BuildContext(bundle);
        if (context.Length == 0)
        {
            return "No research data was gathered (no search providers configured or all returned empty).";
        }

        var prompt = PromptTemplates.Analyze(task, geo, context);
        return await _llm.CompleteTextAsync(PromptTemplates.AnalystSystem, prompt, cancellationToken).ConfigureAwait(false);
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
        var sentiments = posts.Select(p => SimpleSentiment(p.Text));
        return SentimentSummary.FromOpinions(sentiments);
    }

    private static Sentiment SimpleSentiment(string text)
    {
        var n = ArabicNormalizer.Normalize(text);
        var positive = new[] { "ممتاز", "رائع", "جيد", "افضل", "احسن", "good", "great", "best", "excellent" };
        var negative = new[] { "سيء", "رديء", "مشكله", "خربان", "bad", "worst", "broken", "problem" };
        var pos = positive.Count(w => n.Contains(ArabicNormalizer.Normalize(w), StringComparison.Ordinal));
        var neg = negative.Count(w => n.Contains(ArabicNormalizer.Normalize(w), StringComparison.Ordinal));
        return pos > neg ? Sentiment.Positive : neg > pos ? Sentiment.Negative : Sentiment.Neutral;
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
