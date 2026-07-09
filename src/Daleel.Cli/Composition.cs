using Daleel.Agent;
using Daleel.Agent.Llm;
using Daleel.Apify;
using Daleel.Core.Llm;
using Daleel.Core.Pipeline;
using Daleel.Pipeline;
using Daleel.Search;
using Daleel.Search.Abstractions;
using Daleel.Search.Providers;

namespace Daleel.Cli;

/// <summary>
/// Composition root for the agent-backed CLI commands. Inspects environment variables and
/// wires up whichever providers have credentials, so the tool runs with as little or as
/// much capability as the user has configured.
/// </summary>
internal static class Composition
{
    /// <summary>True when an LLM provider key is available (the agent's hard requirement).</summary>
    public static bool HasLlm =>
        Env("OPENROUTER_API_KEY") is not null ||
        Env("OPENAI_API_KEY") is not null ||
        Env("ANTHROPIC_API_KEY") is not null;

    /// <summary>
    /// Selects an LLM client, preferring OpenRouter (one key, every model), then OpenAI,
    /// then Anthropic. <paramref name="model"/>, when supplied, overrides the chosen
    /// provider's default model — most useful with OpenRouter (e.g. "anthropic/claude-sonnet-4").
    /// </summary>
    public static ILlmClient BuildLlm(string? model = null)
    {
        if (Env("OPENROUTER_API_KEY") is not null)
        {
            return new OpenRouterClient(model: model);
        }

        if (Env("OPENAI_API_KEY") is not null)
        {
            return model is not null ? new OpenAiClient(model: model) : new OpenAiClient();
        }

        if (Env("ANTHROPIC_API_KEY") is not null)
        {
            return model is not null ? new AnthropicClient(model: model) : new AnthropicClient();
        }

        throw new InvalidOperationException(
            "Set OPENROUTER_API_KEY, OPENAI_API_KEY or ANTHROPIC_API_KEY to use the agent.");
    }

    /// <summary>Builds a fully-wired <see cref="AgentService"/> from the environment.</summary>
    public static AgentService BuildAgent(string defaultGeo, Action<string>? log = null, string? model = null)
    {
        var llm = BuildLlm(model);

        ISearchProvider? search = BuildSearch(log);

        IPlacesProvider? places = Env("GOOGLE_PLACES_API_KEY") is not null ? new GooglePlacesProvider() : null;

        IScrapeProvider? scraper = BuildScraper();

        IPostFetcher? social = Env("APIFY_TOKEN") is not null
            ? new ApifyPostFetcher(new ApifyClient())
            : null;

        OpinionExtractor opinions = new(llm);

        var options = new AgentOptions { DefaultGeo = defaultGeo, Log = log };

        return new AgentService(llm, options, search, places, scraper, social, matcher: null, opinions);
    }

    /// <summary>
    /// Discovery chain: SerpAPI → Bing → the no-vendor-quota browser-SERP fallback, wrapped in a
    /// <see cref="SearchRouter"/> so a primary outage (notably SerpAPI monthly-quota exhaustion)
    /// fails over instead of degrading web discovery to a silent empty. Mirrors AgentFactory.
    /// </summary>
    public static ISearchProvider? BuildSearch(Action<string>? log = null)
    {
        var chain = new List<ISearchProvider>();
        if (Env("SERPAPI_KEY") is not null)
        {
            chain.Add(new SerpApiProvider());
        }
        if (Env("BING_SEARCH_KEY") is not null)
        {
            chain.Add(new BingProvider());
        }
        if (BuildCloudflareBrowser() is { } browser)
        {
            chain.Add(new BrowserSearchProvider(browser));
        }

        return chain.Count switch
        {
            0 => null,
            1 => chain[0],
            _ => new SearchRouter(chain.ToArray(), log is null ? null : hop =>
                log($"Discovery: {hop.FromProvider} unavailable ({hop.Reason}) — falling back to {hop.ToProvider}."))
        };
    }

    /// <summary>The Cloudflare edge browser when its creds are set, else null.</summary>
    private static CloudflareBrowserProvider? BuildCloudflareBrowser() =>
        Env("CLOUDFLARE_ACCOUNT_ID") is not null && Env("CLOUDFLARE_API_TOKEN") is not null
            ? new CloudflareBrowserProvider()
            : null;

    /// <summary>Builds the scrape router (Context.dev → Cloudflare) from whatever is configured.</summary>
    public static IScrapeProvider? BuildScraper()
    {
        var chain = new List<IScrapeProvider>();
        if (Env("CONTEXT_DEV_API_KEY") is not null)
        {
            chain.Add(new ContextDevProvider());
        }
        if (BuildCloudflareBrowser() is { } browser)
        {
            chain.Add(browser);
        }

        return chain.Count switch
        {
            0 => null,
            1 => chain[0],
            _ => new ScrapeRouter(chain.ToArray())
        };
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
