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

    public async Task<Store?> ResearchStoreAsync(string storeName, string? geo, CancellationToken ct = default)
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

        var context = await GatherStoreContextAsync(storeName, geo, ct).ConfigureAwait(false);
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
        var domain = GuessDomain(brandName);

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

    private async Task<string> GatherStoreContextAsync(string storeName, string? geo, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Store: {storeName}{(string.IsNullOrWhiteSpace(geo) ? "" : $" ({geo})")}");
        await AppendScrapeAsync($"https://{GuessDomain(storeName)}", sb, ct).ConfigureAwait(false);
        return sb.ToString();
    }

    private async Task AppendScrapeAsync(string url, StringBuilder sb, CancellationToken ct)
    {
        // url is built from a guessed/LLM-derived domain — refuse internal targets (SSRF) before scraping.
        // The scrape runs on the Context.dev edge, so the DNS-free literal/localhost check is the right layer.
        if (!SsrfGuard.IsSafePublicUrl(url))
        {
            _logger.LogDebug("Skipped scrape for unsafe url {Url}", url);
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
    /// Crude name → homepage heuristic (lower-case, strip non-alphanumerics, append ".com"). The LLM
    /// also brings world knowledge, so even a missed guess yields a usable profile from thin context.
    /// </summary>
    internal static string GuessDomain(string name)
    {
        var slug = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return slug.Length == 0 ? "example.com" : $"{slug}.com";
    }
}
