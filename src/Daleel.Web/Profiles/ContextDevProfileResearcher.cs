using System.Text;
using Daleel.Search.Abstractions;
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

    public ContextDevProfileResearcher(IAgentFactory factory, ILogger<ContextDevProfileResearcher> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public bool IsAvailable =>
        _factory.HasLlm() && _factory.Resolve("CONTEXT_DEV_API_KEY") is not null;

    public async Task<Brand?> ResearchBrandAsync(string brandName, string? geo, CancellationToken ct = default)
    {
        var llm = _factory.TryBuildLlm();
        if (llm is null)
        {
            return null;
        }

        var contextDev = TryBuildContextDev();
        var context = contextDev is null
            ? string.Empty
            : await GatherBrandContextAsync(contextDev, brandName, ct).ConfigureAwait(false);

        return await new ProfileSynthesizer(llm).SynthesizeBrandAsync(brandName, context, ct).ConfigureAwait(false);
    }

    public async Task<Store?> ResearchStoreAsync(string storeName, string? geo, CancellationToken ct = default)
    {
        var llm = _factory.TryBuildLlm();
        if (llm is null)
        {
            return null;
        }

        var contextDev = TryBuildContextDev();
        var context = contextDev is null
            ? string.Empty
            : await GatherStoreContextAsync(contextDev, storeName, geo, ct).ConfigureAwait(false);

        return await new ProfileSynthesizer(llm).SynthesizeStoreAsync(storeName, context, ct).ConfigureAwait(false);
    }

    private ContextDevProvider? TryBuildContextDev() =>
        _factory.Resolve("CONTEXT_DEV_API_KEY") is { } key ? new ContextDevProvider(key) : null;

    private async Task<string> GatherBrandContextAsync(
        ContextDevProvider contextDev, string brandName, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var domain = GuessDomain(brandName);

        try
        {
            var profile = await contextDev.GetBrandAsync(domain, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(profile.Name)) sb.AppendLine($"Brand: {profile.Name}");
            if (!string.IsNullOrWhiteSpace(profile.Description)) sb.AppendLine(profile.Description);
            if (!string.IsNullOrWhiteSpace(profile.Industry)) sb.AppendLine($"Industry: {profile.Industry}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Context.dev brand lookup failed for {Domain}", domain);
        }

        await AppendScrapeAsync(contextDev, $"https://{domain}", sb, ct).ConfigureAwait(false);
        return sb.ToString();
    }

    private async Task<string> GatherStoreContextAsync(
        ContextDevProvider contextDev, string storeName, string? geo, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Store: {storeName}{(string.IsNullOrWhiteSpace(geo) ? "" : $" ({geo})")}");
        await AppendScrapeAsync(contextDev, $"https://{GuessDomain(storeName)}", sb, ct).ConfigureAwait(false);
        return sb.ToString();
    }

    private async Task AppendScrapeAsync(
        ContextDevProvider contextDev, string url, StringBuilder sb, CancellationToken ct)
    {
        try
        {
            var page = await contextDev.ScrapeAsync(url, ScrapeFormat.Markdown, ct).ConfigureAwait(false);
            if (page.Success && page.Content.Length > 0)
            {
                sb.AppendLine(page.Content.Length <= 4000 ? page.Content : page.Content[..4000]);
            }
        }
        catch (Exception ex)
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
