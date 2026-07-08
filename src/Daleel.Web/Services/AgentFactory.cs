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

    /// <summary>Progress sink — streamed to the UI as the agent works.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Halal content-filter strictness for this request (default: Strict).</summary>
    public FilterStrictness Strictness { get; init; } = FilterStrictness.Strict;

    /// <summary>Whitelist keys (URLs / content hashes) admins have un-filtered; these bypass moderation.</summary>
    public IReadOnlyCollection<string>? ModerationWhitelist { get; init; }

    /// <summary>
    /// The effective keyword categories (defaults + dynamic rule overrides) compiled by the
    /// policy snapshot; null falls back to the static defaults.
    /// </summary>
    public IReadOnlyList<ContentFilter.Category>? ModerationCategories { get; init; }

    /// <summary>Moderation thresholds, typically feedback-tuned via <c>IModerationPolicyProvider</c>.</summary>
    public HalalPolicy? HalalPolicy { get; init; }

    /// <summary>Vision screening of individual result images (flagged images are stripped, items kept).</summary>
    public IHalalImageClassifier? ImageClassifier { get; init; }

    /// <summary>Optional observer that records every external API call (timing + cost).</summary>
    public Daleel.Core.Observability.IApiCallObserver? ApiObserver { get; init; }

    /// <summary>Pricing used to estimate per-call cost (defaults to built-in rates).</summary>
    public Daleel.Core.Observability.CostEstimator? CostEstimator { get; init; }

    /// <summary>Optional cache for provider responses. When set, each search provider is wrapped so an
    /// exact provider+query+geo repeat is served from cache instead of a paid external call.</summary>
    public Daleel.Core.Caching.ICacheStore? Cache { get; init; }

    /// <summary>How long cached provider responses stay valid (defaults to 30 days).</summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Per-call-site model overrides (call-site key → OpenRouter model id), resolved from config by the
    /// caller. A missing/blank entry falls back to that call-site's registry default. This is what lets
    /// each pipeline step (planner, extraction, analyst, …) run a different model for cost tuning.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CallSiteModels { get; init; }
}

/// <summary>A snapshot of which capabilities the server environment enables.</summary>
public sealed record ProviderStatus(bool Llm, bool WebSearch, bool Places, bool Scraper, bool Social);

/// <summary>Builds fully-wired <see cref="AgentService"/> instances on demand (the DI analogue of the CLI's Composition root).</summary>
public interface IAgentFactory
{
    /// <summary>True when an LLM key (the agent's hard requirement) is resolvable.</summary>
    bool HasLlm();

    /// <summary>Describes which providers will be active given the server environment.</summary>
    ProviderStatus Describe();

    /// <summary>Builds an agent for one request. Throws if no LLM key is available.</summary>
    AgentService Build(AgentRequest request);

    /// <summary>
    /// Builds a bare LLM client from the resolved keys, or null when none is configured. Used by
    /// collaborators that need the LLM outside a full agent (e.g. the profile researcher and the
    /// background refresh job), so they can degrade gracefully rather than throw.
    /// </summary>
    ILlmClient? TryBuildLlm(string? model = null);

    /// <summary>Resolves a provider key from the server environment, or null when unset.</summary>
    string? Resolve(string name);
}

/// <summary>
/// Mirrors <c>Daleel.Cli.Composition</c> but is DI-registered. All provider keys come from the
/// server environment — there are no per-user keys. Provider construction is otherwise identical,
/// so the web and CLI build the same agent.
/// </summary>
public sealed class AgentFactory : IAgentFactory
{
    // Lazily resolved (never a ctor dependency): IProviderApi itself depends on IAgentFactory, so a
    // constructor injection here would be a DI cycle. Build() runs long after the container exists.
    private readonly IServiceProvider? _services;

    public AgentFactory(IServiceProvider? services = null) => _services = services;

    public string? Resolve(string name)
    {
        // Operator-managed configuration only (no per-user keys): the credential VAULT's cached
        // snapshot wins (admin-managed, rotatable at runtime), the server environment is the
        // bootstrap fallback. Sync by design — this sits on hot paths.
        if (_services?.GetService(typeof(Daleel.Web.Data.ICredentialVault))
                is Daleel.Web.Data.ICredentialVault vault)
        {
            // CF_{X}_WORKER_TOKEN names alias the authority-minted worker bearer for THIS
            // environment (worker:daleel-{x}-worker[-qa]) — the rotatable vault value must win
            // over any deploy-time env token, or rotation would strand these resolvers on a
            // token the worker no longer accepts.
            if (Daleel.Web.Cloudflare.WorkerNames.BearerAlias(
                    name, Environment.GetEnvironmentVariable("DALEEL_ENV")) is { } bearerName &&
                vault.TryGetCached(bearerName) is { } bearer)
            {
                return bearer;
            }

            if (vault.TryGetCached(name) is { } fromVault)
            {
                return fromVault;
            }
        }

        var env = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }

    public bool HasLlm() =>
        Resolve("OPENROUTER_API_KEY") is not null ||
        Resolve("OPENAI_API_KEY") is not null ||
        Resolve("ANTHROPIC_API_KEY") is not null;

    public ProviderStatus Describe() => new(
        Llm: HasLlm(),
        // The browser-SERP fallback also counts as web discovery: with Cloudflare creds, discovery
        // survives even when no search-vendor key is present.
        WebSearch: Resolve("SERPAPI_KEY") is not null || Resolve("BING_SEARCH_KEY") is not null ||
                   HasCloudflareBrowser(),
        Places: Resolve("GOOGLE_PLACES_API_KEY") is not null,
        Scraper: Resolve("CONTEXT_DEV_API_KEY") is not null || HasCloudflareBrowser(),
        Social: Resolve("APIFY_TOKEN") is not null);

    private bool HasCloudflareBrowser() =>
        Resolve("CLOUDFLARE_ACCOUNT_ID") is not null && Resolve("CLOUDFLARE_API_TOKEN") is not null;

    public AgentService Build(AgentRequest request)
    {
        // Fail fast when no LLM key is configured: the routing client below builds its backing clients
        // lazily (on the first completion), so validate the hard requirement up-front rather than deep
        // inside a search run.
        if (!HasLlm())
        {
            throw new InvalidOperationException(
                "No LLM key available. Set OPENROUTER_API_KEY (recommended), OPENAI_API_KEY or ANTHROPIC_API_KEY " +
                "as a server environment variable, or enter one on the Settings page.");
        }

        // Per-call-site model routing: each pipeline step (planner, extraction, analyst, …) resolves its
        // own model from config (CallSiteModels) → registry default, so steps can be cost-tuned
        // independently. One backing client is built per distinct model, logging-wrapped when an observer
        // is present so each call is still metered — with its call-site stamped on (LoggingLlmClient reads
        // the ambient LlmCallSiteScope the pipeline opens around each call).
        var estimator = request.CostEstimator ?? new Daleel.Core.Observability.CostEstimator();
        var observer = request.ApiObserver;

        ILlmClient BuildModelClient(string model)
        {
            ILlmClient client = BuildLlm(model);
            return observer is null
                ? client
                : new Daleel.Agent.Instrumentation.LoggingLlmClient(client, observer, estimator);
        }

        string ModelForCallSite(string callSite) =>
            request.CallSiteModels is { } configured &&
            configured.TryGetValue(callSite, out var cfg) && !string.IsNullOrWhiteSpace(cfg)
                ? cfg
                : LlmCallSites.DefaultFor(callSite);

        ILlmClient llm = new RoutingLlmClient(
            ModelForCallSite,
            BuildModelClient,
            defaultModel: string.IsNullOrWhiteSpace(request.Model) ? LlmCallSites.DefaultModel : request.Model!);

        // Edge search proxy (search-worker, doc §3.5): when configured, the SerpAPI/Places calls run
        // through the worker — which injects the vendor key (held as a worker secret) and serves hot
        // repeats from its KV cache — while ALL parsing stays in the untouched providers here (Axis A:
        // relocated execution, identical behavior). Unconfigured ⇒ providers hit the vendors directly.
        var searchProxy = EdgeSearchClient();

        ISearchProvider? search = BuildSearch(searchProxy, request.Log);

        IPlacesProvider? places =
            Resolve("GOOGLE_PLACES_API_KEY") is { } gp ? new GooglePlacesProvider(gp, EdgeSearchClient())
            : EdgeSearchClient() is { } placesProxy ? new GooglePlacesProvider("edge-proxied", placesProxy)
            : null;

        IScrapeProvider? scraper = BuildScraper();

        IPostFetcher? social =
            Resolve("APIFY_TOKEN") is { } apify ? new ApifyPostFetcher(new ApifyClient(apify)) : null;

        // Wrap the non-LLM providers in a logging decorator when an observer is supplied, so every
        // external call is timed, cost-estimated, and streamed. The LLM is already per-model
        // logging-wrapped inside the router's factory above.
        if (observer is not null)
        {
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
        // SCALE-TO-HUNDREDS breadth. The pipeline fan-outs are already uncapped (PipelineLimits); the real
        // limiter on the model count is how many pages get READ and how many DIVERSE queries run, both of
        // which live here in AgentOptions. Raised from the VPS-era defaults and made env-tunable so an
        // operator can trade coverage against cost/latency + the SerpAPI hourly cap without a redeploy.
        var options = new AgentOptions
        {
            DefaultGeo = request.Geo,
            Log = request.Log,
            Language = request.Language,
            MaxQueriesPerKind = EnvInt("SEARCH_MAX_QUERIES_PER_KIND", 24),          // was 14
            MaxUrlsToRead = EnvInt("SEARCH_MAX_URLS_TO_READ", 20),                  // was 6
            MaxListingUrls = EnvInt("SEARCH_MAX_LISTING_URLS", 40),                 // was 12
            WebDiscoveryResultsPerQuery = EnvInt("SEARCH_WEB_DISCOVERY_PER_QUERY", 30), // was 20
        };
        var filter = new ContentFilter(request.Strictness, request.ModerationWhitelist, request.ModerationCategories);

        // The moderation pipeline: deterministic keyword baseline + LLM adjudication over the SAME
        // (already logging-wrapped) client, so classification calls are metered and cost-capped like
        // every other LLM call. Vision image screening is optional and injected by the caller.
        // The filter-worker A/B SHADOW (doc §6 Phase 3): when the edge filter host is configured,
        // decorate both classifiers so every moderation batch also produces an agreement log line —
        // the labeled dataset that decides whether edge classification can ever take default
        // traffic. The inner classifiers stay authoritative; the shadow is detached and swallowed.
        IHalalClassifier classifier = new LlmHalalClassifier(llm);
        var imageClassifier = request.ImageClassifier;
        if (_services?.GetService(typeof(IProviderApi)) is IProviderApi { HasEdgeFilter: true } providerApi &&
            _services.GetService(typeof(ILogger<AgentFactory>)) is ILogger<AgentFactory> shadowLog)
        {
            classifier = new Daleel.Web.Moderation.ShadowHalalClassifier(classifier, providerApi, shadowLog);
            if (imageClassifier is not null)
            {
                imageClassifier = new Daleel.Web.Moderation.ShadowHalalImageClassifier(imageClassifier, providerApi, shadowLog);
            }
        }

        var moderator = new HalalModerator(
            filter,
            classifier: classifier,
            imageClassifier: imageClassifier,
            policy: request.HalalPolicy,
            log: request.Log);

        return new AgentService(llm, options, search, places, scraper, social, matcher: null, opinions, filter,
            moderator);
    }

    /// <summary>Reads a positive int from the environment, else the fallback. Used for the env-tunable
    /// search-breadth knobs; a non-positive or unparseable value falls back rather than zeroing coverage.</summary>
    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    /// <summary>Selects an LLM client, preferring OpenRouter (one key, every model), then OpenAI, then Anthropic.</summary>
    private ILlmClient BuildLlm(string? model) =>
        TryBuildLlm(model) ?? throw new InvalidOperationException(
            "No LLM key available. Set OPENROUTER_API_KEY (recommended), OPENAI_API_KEY or ANTHROPIC_API_KEY " +
            "as a server environment variable, or enter one on the Settings page.");

    public ILlmClient? TryBuildLlm(string? model = null)
    {
        if (Resolve("OPENROUTER_API_KEY") is { } openrouter)
        {
            return new OpenRouterClient(openrouter, model);
        }

        if (Resolve("OPENAI_API_KEY") is { } openai)
        {
            return string.IsNullOrWhiteSpace(model) ? new OpenAiClient(openai) : new OpenAiClient(openai, model);
        }

        if (Resolve("ANTHROPIC_API_KEY") is { } anthropic)
        {
            return string.IsNullOrWhiteSpace(model) ? new AnthropicClient(anthropic) : new AnthropicClient(anthropic, model);
        }

        return null;
    }

    /// <summary>
    /// A fresh HttpClient pointed at the search-worker (bearer pre-set) when CF_SEARCH_WORKER_URL +
    /// CF_SEARCH_WORKER_TOKEN are configured, else null. One client per provider instance — they share
    /// the pooled <see cref="Daleel.Search.Http.SharedHttpHandler"/> underneath, so sockets are reused.
    /// </summary>
    private HttpClient? EdgeSearchClient()
    {
        if (Resolve("CF_SEARCH_WORKER_URL") is not { } url ||
            Resolve("CF_SEARCH_WORKER_TOKEN") is not { } token ||
            !Uri.TryCreate(url, UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var client = Daleel.Search.Http.SharedHttpHandler.CreateClient();
        client.BaseAddress = baseUri;
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Builds the discovery search chain: SerpAPI (direct key or edge-proxied) → Bing → the
    /// no-vendor-quota browser-SERP fallback, wrapped in a <see cref="SearchRouter"/> so a primary
    /// outage — most pressingly SerpAPI monthly-quota exhaustion, which returns non-2xx and would
    /// otherwise leave web discovery a silent empty — fails over to the next source. A single
    /// configured provider is returned bare; none configured returns null.
    /// </summary>
    private ISearchProvider? BuildSearch(HttpClient? searchProxy, Action<string>? log)
    {
        var chain = new List<ISearchProvider>();

        if (Resolve("SERPAPI_KEY") is { } serp)
        {
            chain.Add(new SerpApiProvider(serp, searchProxy));
        }
        // The key can live ONLY on the worker: the placeholder satisfies the provider's ctor and the
        // worker replaces whatever api_key the query carries with its own secret.
        else if (searchProxy is not null)
        {
            chain.Add(new SerpApiProvider("edge-proxied", EdgeSearchClient()));
        }

        if (Resolve("BING_SEARCH_KEY") is { } bing)
        {
            chain.Add(new BingProvider(bing));
        }

        // Last resort: render a SERP with the edge browser. Needs only the Cloudflare creds the
        // scraper already uses — no search-vendor quota — so discovery survives SerpAPI exhaustion.
        if (BuildCloudflareBrowser() is { } browser)
        {
            chain.Add(new BrowserSearchProvider(browser));
        }

        return chain.Count switch
        {
            0 => null,
            1 => chain[0],
            _ => new SearchRouter(chain.ToArray(), Failover(log))
        };
    }

    /// <summary>Streams each discovery failover hop to the request's progress log — the same seam the
    /// event spine taps to persist it — or null when there is no log sink to write to.</summary>
    private static Action<SearchFailover>? Failover(Action<string>? log) => log is null
        ? null
        : hop => log($"Discovery: {hop.FromProvider} unavailable ({hop.Reason}) — falling back to {hop.ToProvider}.");

    /// <summary>The Cloudflare edge browser when its creds are set, else null. Shared conceptually by
    /// the scrape chain and the browser-SERP discovery fallback so both are gated on the same creds.</summary>
    private CloudflareBrowserProvider? BuildCloudflareBrowser() =>
        Resolve("CLOUDFLARE_ACCOUNT_ID") is { } cfId && Resolve("CLOUDFLARE_API_TOKEN") is { } cfToken
            ? new CloudflareBrowserProvider(cfId, cfToken)
            : null;

    /// <summary>Builds the scrape router (Context.dev → Cloudflare) from whatever is configured.</summary>
    private IScrapeProvider? BuildScraper()
    {
        var chain = new List<IScrapeProvider>();
        if (Resolve("CONTEXT_DEV_API_KEY") is { } ctx)
        {
            chain.Add(new ContextDevProvider(ctx));
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
}
