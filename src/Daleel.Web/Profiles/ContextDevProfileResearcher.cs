using System.Text;
using Daleel.Core.Geo;
using Daleel.Search.Abstractions;
using Daleel.Search.Http;
using Daleel.Search.Providers;
using Daleel.Web.Data;
using Daleel.Web.Services;

namespace Daleel.Web.Profiles;

/// <summary>
/// The production <see cref="IProfileResearcher"/>: gathers brand/store content via Context.dev
/// (brand-intelligence endpoint + page scrape) and synthesizes it into a profile with the LLM.
/// Keys are resolved from the server environment through <see cref="IAgentFactory"/>, so it shares
/// the exact same key-resolution as the search agent.
/// </summary>
/// <remarks>
/// Research is best-effort: Context.dev failures (unknown domain, network) are swallowed and the
/// synthesizer falls back to the LLM's own knowledge from whatever thin context was gathered. When
/// no LLM or Context.dev key is configured this returns null and the profile service degrades.
/// </remarks>
public sealed class ContextDevProfileResearcher : IProfileResearcher
{
    private readonly IAgentFactory _factory;
    private readonly ILogger<ContextDevProfileResearcher> _logger;
    private readonly IProviderApi _providers;

    public ContextDevProfileResearcher(
        IAgentFactory factory, ILogger<ContextDevProfileResearcher> logger, IProviderApi? providers = null)
    {
        _factory = factory;
        _logger = logger;
        // Optional so existing test wiring keeps working; production DI always supplies the gateway.
        _providers = providers ?? new ProviderApi(factory);
    }

    public bool IsAvailable => _factory.HasLlm() && _providers.HasScraper;

    public async Task<Brand?> ResearchBrandAsync(string brandName, string? geo, CancellationToken ct = default)
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

        var context = await GatherBrandContextAsync(brandName, ct).ConfigureAwait(false);
        return await new ProfileSynthesizer(llm).SynthesizeBrandAsync(brandName, context, ct).ConfigureAwait(false);
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

        var context = await GatherStoreContextAsync(storeName, geo, siteUrlHint, ct).ConfigureAwait(false);
        var store = await new ProfileSynthesizer(llm).SynthesizeStoreAsync(storeName, context, ct).ConfigureAwait(false);

        // Fall back to contact details scraped from the store page when the LLM didn't surface them.
        store.Phone ??= ContactExtractor.FirstPhone(context);
        store.Email ??= ContactExtractor.FirstEmail(context);

        // Cross-reference Google Places for authoritative location, coordinates, hours, rating + place id.
        await VerifyOnPlacesAsync(store, storeName, geo, ct).ConfigureAwait(false);
        return store;
    }

    /// <summary>
    /// Verifies a store against Google Places: finds the best-matching place near the market centre,
    /// then pulls its full details (hours/reviews need the detail field mask) and stamps the
    /// authoritative coordinates, opening hours, Google rating and place id onto the profile. Best
    /// effort — a missing Places key or no confident match simply leaves the profile un-verified.
    /// </summary>
    private async Task VerifyOnPlacesAsync(Store store, string storeName, string? geo, CancellationToken ct)
    {
        // Through the gateway — metered by construction (this was one of the two unmetered direct
        // provider constructions the audit caught).
        if (!_providers.HasPlaces)
        {
            return;
        }

        var profile = GeoProfiles.ResolveOrDefault(geo);
        try
        {
            var matches = await _providers
                .SearchPlacesAsync(storeName, profile.Center, 15000, profile.PrimaryLanguage, ct)
                .ConfigureAwait(false);

            // Prefer a name-matching place; fall back to the closest result only if none matches.
            var match = matches.FirstOrDefault(m => NameMatches(storeName, m.Name));
            if (match is null || string.IsNullOrEmpty(match.PlaceId))
            {
                return;
            }

            // The text-search field mask omits opening hours/reviews, so re-fetch full details.
            var detail = await _providers.GetPlaceDetailsAsync(match.PlaceId, ct).ConfigureAwait(false) ?? match;

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Google Places verification failed for {Store}", storeName);
        }
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

    private async Task<string> GatherBrandContextAsync(string brandName, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (GuessDomain(brandName) is not { } domain)
        {
            // Nothing guessable (e.g. an Arabic-only brand name): every provider call from here would be
            // aimed at a hostname we invented. Spend nothing.
            _logger.LogDebug("No guessable domain for brand {Brand} — skipping provider lookups.", brandName);
            return sb.ToString();
        }

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

        await AppendScrapeAsync($"https://{domain}", sb, ct).ConfigureAwait(false);
        return sb.ToString();
    }

    private async Task<string> GatherStoreContextAsync(
        string storeName, string? geo, string? siteUrlHint, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Store: {storeName}{(string.IsNullOrWhiteSpace(geo) ? "" : $" ({geo})")}");
        // Prefer an actor-VERIFIED site (the LLM confirmed it is really this store's own domain) over the
        // GuessDomain heuristic (strip punctuation + ".com"), which misses rebranded/abbreviated domains.
        // When neither exists there is no site to read — scraping an invented hostname only costs money.
        var url = !string.IsNullOrWhiteSpace(siteUrlHint)
            ? siteUrlHint!
            : GuessDomain(storeName) is { } guessed ? $"https://{guessed}" : null;

        if (url is null)
        {
            _logger.LogDebug("No verified or guessable site for store {Store} — skipping scrape.", storeName);
            return sb.ToString();
        }

        await AppendScrapeAsync(url, sb, ct).ConfigureAwait(false);
        return sb.ToString();
    }

    private async Task AppendScrapeAsync(string url, StringBuilder sb, CancellationToken ct)
    {
        // url is built from a guessed/LLM-derived domain, so VALIDATE THE SITE BEFORE SENDING IT.
        // IsSafePublicUrlAsync resolves DNS: it refuses internal targets (SSRF) *and* returns false for a
        // host that does not resolve at all. That second half is what matters here — the scraper bills us
        // the same to answer "DNS resolution failed" as it does to return a page, and a guessed hostname
        // usually does not exist. One free local lookup replaces a paid empty round-trip. (The older
        // DNS-free check was chosen because the fetch happens on the provider's edge; that reasoning
        // ignored the cost of the call.)
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

    /// <summary>
    /// Crude name → homepage heuristic (lower-case, strip to ASCII letters/digits, append ".com"), or
    /// NULL when the name gives nothing to guess with. Two traps closed here:
    /// <list type="bullet">
    /// <item><c>char.IsLetterOrDigit</c> is true for Arabic script, so a store named "كارفور ماركت" used
    /// to become the hostname <c>كارفورماركت.com</c> — a host that never existed and that we then PAID a
    /// scraper to resolve. A hostname label is ASCII letters/digits/hyphen; a non-Latin display name is
    /// simply not guessable into one.</item>
    /// <item>The old empty-slug fallback was the literal <c>example.com</c> — a paid scrape of the IANA
    /// example domain.</item>
    /// </list>
    /// Callers must skip the lookup entirely when this returns null.
    /// </summary>
    internal static string? GuessDomain(string name)
    {
        var slug = new string(name
            .Where(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
            .ToArray()).ToLowerInvariant();
        return slug.Length < 3 ? null : $"{slug}.com";
    }
}
