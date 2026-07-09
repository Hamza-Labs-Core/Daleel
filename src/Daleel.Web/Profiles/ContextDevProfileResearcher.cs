using System.Text;
using Daleel.Agent;
using Daleel.Core.Caching;
using Daleel.Core.Geo;
using Daleel.Core.Llm;
using Daleel.Core.Models;
using Daleel.Core.Observability;
using Daleel.Search.Abstractions;
using Daleel.Search.Http;
using Daleel.Search.Providers;
using Daleel.Web.Data;
using Daleel.Web.Pipeline.Enrichment.Actor;
using Daleel.Web.Services;

namespace Daleel.Web.Profiles;

/// <summary>
/// The production <see cref="IProfileResearcher"/>: gathers brand/store content via Context.dev
/// (brand-intelligence endpoint + page scrape) and synthesizes it into a profile with the LLM.
/// Keys are resolved from the server environment through <see cref="IAgentFactory"/>, so it shares
/// the exact same key-resolution as the search agent.
/// </summary>
/// <remarks>
/// <para>
/// Provider calls only ever target a REAL site; a hostname is never fabricated from the entity's
/// display name (a guessed domain that happens to resolve scrapes some unrelated company's site
/// and silently attributes it to the entity — worse than no data). For STORES the URL is
/// established in trust order: Google Places' authoritative website → the caller's saved URL →
/// the site-discovery actor (search → read → confirm; bounded, and only in metered contexts).
/// For BRANDS only the caller's URL is used — brand-site discovery belongs to the enrichment
/// queue's BrandResearchHandler, which persists what it finds (site hierarchy rows) and latches
/// negatives, so running it here too would double-pay for the same discovery. When no real URL
/// exists, the paid lookups are skipped entirely.
/// </para>
/// <para>
/// Research is best-effort: Context.dev failures (unknown domain, network) are swallowed and the
/// synthesizer falls back to the LLM's own knowledge from whatever thin context was gathered. When
/// no LLM or Context.dev key is configured this returns null and the profile service degrades.
/// </para>
/// </remarks>
public sealed class ContextDevProfileResearcher : IProfileResearcher
{
    /// <summary>
    /// Hard wall-clock bound on the store discovery actor. Store research runs inside the search
    /// sub-workflow's fixed budget (75s including the catalogue crawl), and the actor's browser-SERP
    /// tool calls can be slow — a discovery that can't finish in time must yield null and let the
    /// rest of the research pass complete and PERSIST, not cancel the whole pass.
    /// </summary>
    private static readonly TimeSpan DiscoveryBudget = TimeSpan.FromSeconds(20);

    private readonly IAgentFactory _factory;
    private readonly ILogger<ContextDevProfileResearcher> _logger;
    private readonly IProviderApi _providers;
    private readonly StoreSiteActor? _storeSites;
    private readonly IServiceScopeFactory? _scopes;
    private readonly ICacheStore? _cache;

    public ContextDevProfileResearcher(
        IAgentFactory factory, ILogger<ContextDevProfileResearcher> logger, IProviderApi? providers = null,
        StoreSiteActor? storeSites = null, IServiceScopeFactory? scopes = null, ICacheStore? cache = null)
    {
        _factory = factory;
        _logger = logger;
        // Optional so existing test wiring keeps working; production DI always supplies the gateway.
        _providers = providers ?? new ProviderApi(factory);
        // Optional for the same reason: without the actor this researcher simply never discovers a
        // store site on its own — it still uses Places and caller hints, and skips paid calls otherwise.
        _storeSites = storeSites;
        _scopes = scopes;
        _cache = cache;
    }

    public bool IsAvailable => _factory.HasLlm() && _providers.HasScraper;

    public async Task<Brand?> ResearchBrandAsync(
        string brandName, string? geo, CancellationToken ct = default, string? siteUrlHint = null)
    {
        var llm = _factory.TryBuildLlm();
        if (llm is null)
        {
            return null;
        }

        if (!_providers.HasScraper)
        {
            // No research keys configured: honor the documented contract (return null and let the
            // profile service degrade) rather than burning an LLM call on empty context.
            return null;
        }

        // Brands: caller URL or nothing. Discovery is the enrichment queue's job (see class remarks).
        var url = await FirstUsableAsync(new[] { NormalizeUrl(siteUrlHint) }, ct).ConfigureAwait(false);

        var context = await GatherBrandContextAsync(brandName, url, ct).ConfigureAwait(false);
        var brand = await new ProfileSynthesizer(llm).SynthesizeBrandAsync(brandName, context, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(brand.Website) && url is not null)
        {
            brand.Website = url;
        }

        return brand;
    }

    public async Task<Store?> ResearchStoreAsync(
        string storeName, string? geo, CancellationToken ct = default, string? siteUrlHint = null)
    {
        var llm = _factory.TryBuildLlm();
        if (llm is null)
        {
            return null;
        }

        if (!_providers.HasScraper)
        {
            // No research keys configured: honor the documented contract (return null and let the
            // profile service degrade) rather than burning an LLM call on empty context.
            return null;
        }

        // Places runs FIRST (it used to run after synthesis): its authoritative website is the
        // cheapest real-URL source for stores whose display name gives no hostname clue (most
        // Arabic-named local stores), and its details are stamped onto the profile at the end
        // either way — same one lookup, now also feeding the scrape target. It also outranks the
        // caller's saved URL: legacy rows can carry sites the retired name→hostname heuristic
        // invented, and Google's listing is the self-healing correction for them.
        var place = await FindPlaceAsync(storeName, geo, ct).ConfigureAwait(false);

        var url = await FirstUsableAsync(
                      new[] { NormalizeUrl(place?.Website), NormalizeUrl(siteUrlHint) }, ct).ConfigureAwait(false)
                  ?? await DiscoverStoreSiteAsync(storeName, geo, ct).ConfigureAwait(false);

        var context = await GatherStoreContextAsync(storeName, geo, url, ct).ConfigureAwait(false);
        var store = await new ProfileSynthesizer(llm).SynthesizeStoreAsync(storeName, context, ct).ConfigureAwait(false);

        // Fall back to contact details scraped from the store page when the LLM didn't surface them.
        store.Phone ??= ContactExtractor.FirstPhone(context);
        store.Email ??= ContactExtractor.FirstEmail(context);
        if (string.IsNullOrWhiteSpace(store.Website) && url is not null)
        {
            store.Website = url; // the discovered site is a research finding — persist it with the profile
        }

        StampPlaceDetails(store, place);
        return store;
    }

    // ── Store-site discovery: real URLs only ───────────────────────────────────────────────────

    /// <summary>
    /// Store-site discovery as a bounded actor workflow: web search → open candidates → the LLM
    /// confirms the page is the store's OWN site → verified URL, or null when the store has no own
    /// web presence. The result still passes the free DNS/SSRF pre-flight — the loop's answer is
    /// LLM text derived from untrusted pages, so shape-validation alone doesn't make it a safe
    /// scrape target. Runs ONLY inside a metered flow (a search run or enrichment unit): a paid
    /// multi-turn loop that shows up in no cost ledger is not acceptable, so unmetered contexts
    /// (admin refresh outside a job) skip discovery instead. Best-effort; null on any failure or
    /// when <see cref="DiscoveryBudget"/> elapses.
    /// </summary>
    private async Task<string?> DiscoverStoreSiteAsync(string storeName, string? geo, CancellationToken ct)
    {
        if (_storeSites is null)
        {
            return null;
        }

        if (AmbientApiObserver.Observer is null)
        {
            _logger.LogDebug("Skipping store site discovery for {Store} — no metered flow active.", storeName);
            return null;
        }

        try
        {
            if (await BuildActorAgentAsync(geo, ct).ConfigureAwait(false) is not { } agent)
            {
                return null;
            }

            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(DiscoveryBudget);
            var found = await _storeSites
                .FindSiteAsync(agent, storeName, GeoProfiles.ResolveOrDefault(geo), bounded.Token)
                .ConfigureAwait(false);
            return await FirstUsableAsync(new[] { NormalizeUrl(found) }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Store site discovery for {Store} hit its {Budget}s budget.",
                storeName, DiscoveryBudget.TotalSeconds);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Store site discovery failed for {Store}", storeName);
            return null;
        }
    }

    /// <summary>
    /// The actor's agent: pinned to the configured actor model (<c>actor.model</c>, the same knob
    /// the enrichment queue's actors use) with the pin carried through <see cref="AgentRequest.CallSiteModels"/>
    /// — the actor loop's turns run inside the synthesis call-site scope, so the default-model field
    /// alone would be bypassed by the router. Metering rides the ambient per-job observer (the
    /// caller guarantees one is active), and the shared provider cache keeps an exact repeat of an
    /// actor web search free.
    /// </summary>
    private async Task<AgentService?> BuildActorAgentAsync(string? geo, CancellationToken ct)
    {
        try
        {
            var model = await ActorModelAsync(ct).ConfigureAwait(false);
            return _factory.Build(new AgentRequest
            {
                Geo = GeoProfiles.ResolveOrDefault(geo).Key,
                Model = model,
                CallSiteModels = new Dictionary<string, string> { [LlmCallSites.Synthesis.Key] = model },
                ApiObserver = AmbientApiObserver.Observer,
                CostEstimator = AmbientApiObserver.Estimator,
                Cache = _cache,
                CacheTtl = TimeSpan.FromDays(30),
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not build the site-discovery agent");
            return null;
        }
    }

    /// <summary>Reads <c>actor.model</c> through a fresh DI scope (the config service is DbContext-bound).</summary>
    private async Task<string> ActorModelAsync(CancellationToken ct)
    {
        if (_scopes is null)
        {
            return ActorFlags.DefaultModel;
        }

        try
        {
            using var scope = _scopes.CreateScope();
            var config = scope.ServiceProvider.GetService<ISystemConfigService>();
            var model = config is null ? null : await config.GetAsync(ActorFlags.Model, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(model) ? ActorFlags.DefaultModel : model!;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return ActorFlags.DefaultModel;
        }
    }

    /// <summary>
    /// The first candidate URL that is public AND actually resolves — one free local DNS lookup per
    /// candidate instead of a paid provider round-trip to a dead or internal host. Null when none do.
    /// </summary>
    private async Task<string?> FirstUsableAsync(IEnumerable<string?> candidates, CancellationToken ct)
    {
        foreach (var candidate in candidates)
        {
            if (candidate is null)
            {
                continue;
            }

            if (await SsrfGuard.IsSafePublicUrlAsync(candidate, ct).ConfigureAwait(false))
            {
                return candidate;
            }

            _logger.LogDebug("Skipping unsafe or unresolvable site candidate {Url}", candidate);
        }

        return null;
    }

    /// <summary>A Website field may be stored as a bare host — give it a scheme so Uri/guard code can parse it.</summary>
    private static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var s = url.Trim();
        return s.Contains("://", StringComparison.Ordinal) ? s : $"https://{s}";
    }

    // ── Google Places ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the store on Google Places: best name-matching place near the market centre, then its
    /// full details (hours/reviews need the detail field mask). Best effort — a missing Places key
    /// or no confident match returns null and the profile simply stays un-verified.
    /// </summary>
    private async Task<StoreLocation?> FindPlaceAsync(string storeName, string? geo, CancellationToken ct)
    {
        // Through the gateway — metered by construction (this was one of the two unmetered direct
        // provider constructions the audit caught).
        if (!_providers.HasPlaces)
        {
            return null;
        }

        var profile = GeoProfiles.ResolveOrDefault(geo);
        try
        {
            var matches = await _providers
                .SearchPlacesAsync(storeName, profile.Center, 15000, profile.PrimaryLanguage, ct)
                .ConfigureAwait(false);

            // Prefer a name-matching place; a non-matching "closest result" is somebody else's shop.
            var match = matches.FirstOrDefault(m => NameMatches(storeName, m.Name));
            if (match is null || string.IsNullOrEmpty(match.PlaceId))
            {
                return null;
            }

            // The text-search field mask omits opening hours/reviews, so re-fetch full details.
            return await _providers.GetPlaceDetailsAsync(match.PlaceId, ct).ConfigureAwait(false) ?? match;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Google Places lookup failed for {Store}", storeName);
            return null;
        }
    }

    /// <summary>Stamps the authoritative Places details (coordinates, hours, rating, place id) onto the profile.</summary>
    private static void StampPlaceDetails(Store store, StoreLocation? detail)
    {
        if (detail is null)
        {
            return;
        }

        store.GooglePlaceId = detail.PlaceId;
        store.GoogleMapsUrl = detail.GoogleMapsUrl;
        store.GoogleRating = detail.Rating;
        store.GoogleReviewCount = detail.ReviewCount;
        store.Latitude = detail.Location?.Latitude;
        store.Longitude = detail.Location?.Longitude;
        if (detail.OpeningHours.Count > 0)
        {
            store.OpeningHours = detail.OpeningHours.ToList();
        }

        // Places is authoritative for these — but only fill where we don't already have a value.
        store.Address ??= detail.Address;
        store.Phone ??= detail.Phone;
        store.Website ??= detail.Website;
        store.Rating ??= detail.Rating;
    }

    /// <summary>Loose name match (normalized, accent/space-insensitive, either-contains-the-other).</summary>
    public static bool NameMatches(string a, string b)
    {
        static string Norm(string s) =>
            new string((s ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        var x = Norm(a);
        var y = Norm(b);
        return x.Length > 0 && y.Length > 0 && (x.Contains(y) || y.Contains(x));
    }

    // ── Context gathering ──────────────────────────────────────────────────────────────────────

    private async Task<string> GatherBrandContextAsync(string brandName, string? url, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (url is null)
        {
            // No verified site from any source: every provider call from here would be aimed at a
            // hostname we'd have to invent. Spend nothing; the synthesizer works from LLM knowledge.
            _logger.LogDebug("No verified site for brand {Brand} — skipping provider lookups.", brandName);
            return sb.ToString();
        }

        if (DomainOf(url) is { } domain)
        {
            try
            {
                // Through the gateway — metered by construction (ambient per-job observer).
                var profile = await _providers.GetBrandAsync(domain, ct).ConfigureAwait(false);
                if (profile is not null)
                {
                    if (!string.IsNullOrWhiteSpace(profile.Name)) sb.AppendLine($"Brand: {profile.Name}");
                    if (!string.IsNullOrWhiteSpace(profile.Description)) sb.AppendLine(profile.Description);
                    if (!string.IsNullOrWhiteSpace(profile.Industry)) sb.AppendLine($"Industry: {profile.Industry}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Context.dev brand lookup failed for {Domain}", domain);
            }
        }

        await AppendScrapeAsync(url, sb, ct).ConfigureAwait(false);
        return sb.ToString();
    }

    private async Task<string> GatherStoreContextAsync(
        string storeName, string? geo, string? url, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Store: {storeName}{(string.IsNullOrWhiteSpace(geo) ? "" : $" ({geo})")}");
        if (url is null)
        {
            _logger.LogDebug("No verified site for store {Store} — skipping scrape.", storeName);
            return sb.ToString();
        }

        await AppendScrapeAsync(url, sb, ct).ConfigureAwait(false);
        return sb.ToString();
    }

    private async Task AppendScrapeAsync(string url, StringBuilder sb, CancellationToken ct)
    {
        // The URL is real (Places, caller-saved, or actor-verified) but may have died since it was
        // recorded. IsSafePublicUrlAsync resolves DNS: it refuses internal targets (SSRF) *and*
        // returns false for a host that no longer resolves — one free local lookup instead of a
        // paid empty round-trip (the scraper bills the same to answer "DNS resolution failed" as
        // it does to return a page).
        if (!await SsrfGuard.IsSafePublicUrlAsync(url, ct).ConfigureAwait(false))
        {
            _logger.LogDebug("Skipped scrape for unsafe or unresolvable url {Url}", url);
            return;
        }

        try
        {
            // Through the gateway — metered by construction; null when unconfigured or empty.
            var page = await _providers.ScrapePageAsync(url, ScrapeFormat.Markdown, ct).ConfigureAwait(false);
            if (page is { Content.Length: > 0 })
            {
                sb.AppendLine(page.Content.Length <= 4000 ? page.Content : page.Content[..4000]);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Context.dev scrape failed for {Url}", url);
        }
    }

    /// <summary>Bare host of a website URL (scheme-optional, www-stripped), or null.</summary>
    private static string? DomainOf(string? url)
    {
        if (NormalizeUrl(url) is not { } s || !Uri.TryCreate(s, UriKind.Absolute, out var u))
        {
            return null;
        }

        return u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
    }
}
