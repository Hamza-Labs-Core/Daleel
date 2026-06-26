using Daleel.Agent;
using Daleel.Agent.Llm;
using Daleel.Apify;
using Daleel.Core.Llm;
using Daleel.Core.Moderation;
using Daleel.Core.Pipeline;
using Daleel.Pipeline;
using Daleel.Search;
using Daleel.Search.Abstractions;
using Daleel.Search.Providers;

namespace Daleel.Web.Services;

/// <summary>Per-request inputs for building an <see cref="AgentService"/>.</summary>
public sealed record AgentRequest
{
    /// <summary>Market key, e.g. "jordan".</summary>
    public string Geo { get; init; } = "jordan";

    /// <summary>OpenRouter model id to use, or null for the provider default.</summary>
    public string? Model { get; init; }

    /// <summary>BCP-47 language for the analyst summary (e.g. "ar"). Defaults to English.</summary>
    public string Language { get; init; } = "en";

    /// <summary>
    /// User-supplied API keys (from the browser Settings page). Take precedence over the
    /// server's environment variables, letting a visitor bring their own keys.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Keys { get; init; }

    /// <summary>Progress sink — streamed to the UI as the agent works.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Halal content-filter strictness for this request (default: Strict).</summary>
    public FilterStrictness Strictness { get; init; } = FilterStrictness.Strict;

    /// <summary>Optional observer that records every external API call (timing + cost).</summary>
    public Daleel.Core.Observability.IApiCallObserver? ApiObserver { get; init; }

    /// <summary>Pricing used to estimate per-call cost (defaults to built-in rates).</summary>
    public Daleel.Core.Observability.CostEstimator? CostEstimator { get; init; }

    /// <summary>Optional cache for provider responses. When set, each search provider is wrapped so an
    /// exact provider+query+geo repeat is served from cache instead of a paid external call.</summary>
    public Daleel.Core.Caching.ICacheStore? Cache { get; init; }

    /// <summary>How long cached provider responses stay valid (defaults to 30 days).</summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromDays(30);
}

/// <summary>A snapshot of which capabilities are available given the current keys.</summary>
public sealed record ProviderStatus(bool Llm, bool WebSearch, bool Places, bool Scraper, bool Social);

/// <summary>Builds fully-wired <see cref="AgentService"/> instances on demand (the DI analogue of the CLI's Composition root).</summary>
public interface IAgentFactory
{
    /// <summary>True when an LLM key (the agent's hard requirement) is resolvable.</summary>
    bool HasLlm(IReadOnlyDictionary<string, string>? keys = null);

    /// <summary>Describes which providers will be active for the given keys.</summary>
    ProviderStatus Describe(IReadOnlyDictionary<string, string>? keys = null);

    /// <summary>Builds an agent for one request. Throws if no LLM key is available.</summary>
    AgentService Build(AgentRequest request);

    /// <summary>
    /// Builds a bare LLM client from the resolved keys, or null when none is configured. Used by
    /// collaborators that need the LLM outside a full agent (e.g. the profile researcher and the
    /// background refresh job), so they can degrade gracefully rather than throw.
    /// </summary>
    ILlmClient? TryBuildLlm(string? model = null, IReadOnlyDictionary<string, string>? keys = null);

    /// <summary>Resolves a value (user key first, then environment), or null if neither set.</summary>
    string? Resolve(string name, IReadOnlyDictionary<string, string>? keys = null);
}

/// <summary>
/// Mirrors <c>Daleel.Cli.Composition</c> but is DI-registered and resolves keys from a
/// per-request dictionary (browser-stored keys) before falling back to environment variables.
/// Provider construction is otherwise identical, so the web and CLI build the same agent.
/// </summary>
public sealed class AgentFactory : IAgentFactory
{
    public string? Resolve(string name, IReadOnlyDictionary<string, string>? keys = null)
    {
        if (keys is not null && keys.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v))
        {
            return v.Trim();
        }

        var env = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }

    public bool HasLlm(IReadOnlyDictionary<string, string>? keys = null) =>
        Resolve("OPENROUTER_API_KEY", keys) is not null ||
        Resolve("OPENAI_API_KEY", keys) is not null ||
        Resolve("ANTHROPIC_API_KEY", keys) is not null;

    public ProviderStatus Describe(IReadOnlyDictionary<string, string>? keys = null) => new(
        Llm: HasLlm(keys),
        WebSearch: Resolve("SERPAPI_KEY", keys) is not null || Resolve("BING_SEARCH_KEY", keys) is not null,
        Places: Resolve("GOOGLE_PLACES_API_KEY", keys) is not null,
        Scraper: Resolve("CONTEXT_DEV_API_KEY", keys) is not null ||
                 (Resolve("CLOUDFLARE_ACCOUNT_ID", keys) is not null && Resolve("CLOUDFLARE_API_TOKEN", keys) is not null),
        Social: Resolve("APIFY_TOKEN", keys) is not null);

    public AgentService Build(AgentRequest request)
    {
        var keys = request.Keys;
        var llm = BuildLlm(request.Model, keys);

        ISearchProvider? search =
            Resolve("SERPAPI_KEY", keys) is { } serp ? new SerpApiProvider(serp)
            : Resolve("BING_SEARCH_KEY", keys) is { } bing ? new BingProvider(bing)
            : null;

        IPlacesProvider? places =
            Resolve("GOOGLE_PLACES_API_KEY", keys) is { } gp ? new GooglePlacesProvider(gp) : null;

        IScrapeProvider? scraper = BuildScraper(keys);

        IPostFetcher? social =
            Resolve("APIFY_TOKEN", keys) is { } apify ? new ApifyPostFetcher(new ApifyClient(apify)) : null;

        // Wrap every provider in a logging decorator when an observer is supplied, so every
        // external call is timed, cost-estimated, and streamed.
        if (request.ApiObserver is { } observer)
        {
            var estimator = request.CostEstimator ?? new Daleel.Core.Observability.CostEstimator();
            llm = new Daleel.Agent.Instrumentation.LoggingLlmClient(llm, observer, estimator);
            if (search is not null) search = Daleel.Search.Instrumentation.LoggingProviders.Wrap(search, observer, estimator);
            if (places is not null) places = Daleel.Search.Instrumentation.LoggingProviders.Wrap(places, observer, estimator);
            if (scraper is not null) scraper = Daleel.Search.Instrumentation.LoggingProviders.WrapScrape(scraper, observer, estimator);
            if (social is not null) social = Daleel.Search.Instrumentation.LoggingProviders.Wrap(social, observer, estimator, "Apify");
        }

        // Provider-level cache, wrapped OUTSIDE logging so a cache hit skips the real call entirely
        // (and is recorded as a "cache" hit rather than a paid provider call).
        if (request.Cache is { } cache && search is not null)
        {
            search = Daleel.Search.Instrumentation.CachingProviders.Wrap(search, cache, request.CacheTtl, request.ApiObserver);
        }

        var opinions = new OpinionExtractor(llm);
        var options = new AgentOptions { DefaultGeo = request.Geo, Log = request.Log, Language = request.Language };
        var filter = new ContentFilter(request.Strictness);

        return new AgentService(llm, options, search, places, scraper, social, matcher: null, opinions, filter);
    }

    /// <summary>Selects an LLM client, preferring OpenRouter (one key, every model), then OpenAI, then Anthropic.</summary>
    private ILlmClient BuildLlm(string? model, IReadOnlyDictionary<string, string>? keys) =>
        TryBuildLlm(model, keys) ?? throw new InvalidOperationException(
            "No LLM key available. Set OPENROUTER_API_KEY (recommended), OPENAI_API_KEY or ANTHROPIC_API_KEY " +
            "as a server environment variable, or enter one on the Settings page.");

    public ILlmClient? TryBuildLlm(string? model = null, IReadOnlyDictionary<string, string>? keys = null)
    {
        if (Resolve("OPENROUTER_API_KEY", keys) is { } openrouter)
        {
            return new OpenRouterClient(openrouter, model);
        }

        if (Resolve("OPENAI_API_KEY", keys) is { } openai)
        {
            return string.IsNullOrWhiteSpace(model) ? new OpenAiClient(openai) : new OpenAiClient(openai, model);
        }

        if (Resolve("ANTHROPIC_API_KEY", keys) is { } anthropic)
        {
            return string.IsNullOrWhiteSpace(model) ? new AnthropicClient(anthropic) : new AnthropicClient(anthropic, model);
        }

        return null;
    }

    /// <summary>Builds the scrape router (Context.dev → Cloudflare) from whatever is configured.</summary>
    private IScrapeProvider? BuildScraper(IReadOnlyDictionary<string, string>? keys)
    {
        var chain = new List<IScrapeProvider>();
        if (Resolve("CONTEXT_DEV_API_KEY", keys) is { } ctx)
        {
            chain.Add(new ContextDevProvider(ctx));
        }

        if (Resolve("CLOUDFLARE_ACCOUNT_ID", keys) is { } cfId &&
            Resolve("CLOUDFLARE_API_TOKEN", keys) is { } cfToken)
        {
            chain.Add(new CloudflareBrowserProvider(cfId, cfToken));
        }

        return chain.Count switch
        {
            0 => null,
            1 => chain[0],
            _ => new ScrapeRouter(chain.ToArray())
        };
    }
}
