using System.Text.Json;
using Daleel.Core.Models;
using Daleel.Web.Data;
using Daleel.Web.Profiles;
using Daleel.Web.Services;
using Daleel.Web.Storage;
using Elsa.Extensions;
using Elsa.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// Shared entry point for dispatching the LLM <see cref="SiteCrawlWorkflow"/> from a parent sub-workflow
/// (store or brand). Centralises the availability gate — the crawl runs only when Cloudflare Browser
/// Rendering is configured (so JS-heavy sites render) and the runtime kill-switch is on — and the
/// child-dispatch seeding, so both call sites stay identical and can't drift.
/// </summary>
internal static class CrawlDispatch
{
    /// <summary>Runtime kill-switch for the LLM site crawl (defaults on when CF Browser is configured).</summary>
    public const string EnabledFlag = "crawl.enabled";

    /// <summary>
    /// Runs a <see cref="SiteCrawlWorkflow"/> for <paramref name="siteUrl"/> in its own DI scope, seeded from
    /// the <paramref name="parent"/> run, returning the finished crawl state — or null when the crawl is
    /// unavailable (no site URL, no CF Browser, or the switch is off) so the caller can fall back. Bounded by
    /// the store timeout + the outer deadline; best-effort (a per-entity crawl timeout/fault leaves partial
    /// state rather than throwing).
    /// </summary>
    public static async Task<SiteCrawlState?> TryRunAsync(
        ActivityExecutionContext context, string? siteUrl, string siteName, string query,
        SiteKind kind, SubWorkflowState parent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(siteUrl) || !await IsAvailableAsync(context, ct))
        {
            return null;
        }

        var services = context.GetRequiredService<SubWorkflowServices>();
        var scopeFactory = context.GetRequiredService<IServiceScopeFactory>();
        return await SubWorkflowDispatcher.DispatchAsync<SiteCrawlWorkflow, SiteCrawlState>(
            scopeFactory,
            (s, svc) =>
            {
                svc.Agent = services.Agent;
                svc.Progress = services.Progress;
                s.Geo = parent.Geo;
                s.SearchId = parent.SearchId;
                s.SiteUrl = siteUrl!;
                s.SiteName = siteName;
                s.Query = query;
                s.ExpectedKind = kind;
            },
            SubWorkflowDispatcher.StoreResearchTimeout, ct);
    }

    /// <summary>
    /// True when the crawl's renderer (Cloudflare Browser Rendering) is configured — both CF keys resolvable,
    /// mirroring the composition-root gate (<c>AgentFactory</c>) — AND the runtime flag is on. Best-effort: a
    /// config-read failure degrades to "unavailable" so the caller falls back rather than faulting.
    /// </summary>
    private static async Task<bool> IsAvailableAsync(ActivityExecutionContext context, CancellationToken ct)
    {
        if (context.GetService<IAgentFactory>() is not { } factory ||
            factory.Resolve("CLOUDFLARE_ACCOUNT_ID") is null ||
            factory.Resolve("CLOUDFLARE_API_TOKEN") is null)
        {
            return false;
        }

        if (context.GetService<ISystemConfigService>() is not { } config)
        {
            return true; // no config service (test harness) — availability rests on the CF keys being present
        }

        try
        {
            return await config.GetBoolAsync(EnabledFlag, true, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return true; // a config blip mustn't silently disable the crawl — the CF keys already gate it
        }
    }
}

/// <summary>
/// Turns the LLM's <see cref="SiteAssessment"/> into the single concrete listing URL the crawler visits
/// first. Pure and static so the navigation choice — which entry point wins, how the query is injected into
/// a search template — is unit-testable without a workflow or an LLM.
/// </summary>
public static class CrawlNavigation
{
    /// <summary>
    /// Resolves the first listing URL to crawl from the assessment, honouring the LLM's recommended
    /// approach and falling back through the other entry points (search → category → api → sitemap) when
    /// the recommended one isn't available. Returns null when the site exposes no reachable catalogue.
    /// </summary>
    public static string? ResolveEntryPoint(SiteAssessment assessment, string query)
    {
        // Honour the LLM's chosen approach first.
        var chosen = assessment.RecommendedApproach switch
        {
            CrawlApproach.Search => BuildSearchUrl(assessment.SearchUrlTemplate, query),
            CrawlApproach.Category => assessment.ListingUrls.FirstOrDefault(),
            CrawlApproach.Api => assessment.ApiEndpoints.FirstOrDefault(),
            CrawlApproach.Sitemap => assessment.SitemapUrl,
            _ => null
        };
        if (chosen is { Length: > 0 })
        {
            return chosen;
        }

        // Fall back through the remaining entry points in order of usefulness for finding matches: the
        // site's own search (most targeted), then a category page, then a product API, then the sitemap.
        return BuildSearchUrl(assessment.SearchUrlTemplate, query)
            ?? assessment.ListingUrls.FirstOrDefault()
            ?? assessment.ApiEndpoints.FirstOrDefault()
            ?? assessment.SitemapUrl;
    }

    /// <summary>Substitutes the URL-encoded query into a <c>{query}</c> search template, or null when there's no template.</summary>
    public static string? BuildSearchUrl(string? searchTemplate, string query)
    {
        if (string.IsNullOrWhiteSpace(searchTemplate) || !searchTemplate.Contains("{query}", StringComparison.Ordinal))
        {
            return null;
        }

        return searchTemplate.Replace("{query}", Uri.EscapeDataString(query.Trim()), StringComparison.Ordinal);
    }
}

/// <summary>
/// Shared best-effort plumbing for the site-crawl activities: render a page through the metered scraper and
/// archive it to R2 (the save-everything rule), and accumulate discovered products onto the state without
/// duplicates. R2 writes and render failures never fault the crawl.
/// </summary>
internal static class CrawlPipeline
{
    /// <summary>
    /// Renders <paramref name="url"/> through the metered scraper (Context.dev → Cloudflare Browser
    /// Rendering, SSRF-guarded inside <c>ReadPageAsync</c>) and, when it yields content, archives the page
    /// to R2 before returning it. Returns null when nothing renders.
    /// </summary>
    public static async Task<ScrapedPageLike?> RenderAndSaveAsync(
        ActivityExecutionContext context, SiteCrawlState state, string url, CancellationToken ct)
    {
        var services = context.GetRequiredService<SubWorkflowServices>();
        var page = await services.Agent.ReadPageAsync(url, ct);
        if (page is null || string.IsNullOrWhiteSpace(page.Content))
        {
            return null;
        }

        await SaveCrawlPageAsync(context, state, url, page.Provider, page.Content, ct);
        return new ScrapedPageLike(page.Content, string.IsNullOrWhiteSpace(page.Provider) ? "scraper" : page.Provider);
    }

    /// <summary>
    /// Adds <paramref name="products"/> to <see cref="SiteCrawlState.Discovered"/>, skipping ones already
    /// discovered (same detail URL, else same brand+model+name). Returns how many were newly added.
    /// </summary>
    public static int AddDiscovered(SiteCrawlState state, IReadOnlyList<ProductListing> products)
    {
        if (products.Count == 0)
        {
            return 0;
        }

        var seen = new HashSet<string>(state.Discovered.Select(DedupKey), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var p in products)
        {
            if (string.IsNullOrWhiteSpace(p.Name))
            {
                continue;
            }

            if (seen.Add(DedupKey(p)))
            {
                state.Discovered.Add(p);
                added++;
            }
        }

        return added;
    }

    /// <summary>Identity for dedup: the detail URL when present (canonical), else brand+model+name.</summary>
    private static string DedupKey(ProductListing p) =>
        !string.IsNullOrWhiteSpace(p.Url)
            ? p.Url!.Trim().ToLowerInvariant()
            : string.Join('|', new[] { p.Brand, p.Model, p.Name }
                .Select(s => (s ?? string.Empty).Trim().ToLowerInvariant()));

    /// <summary>
    /// Archives a fetched crawl page to R2 under <c>crawl/{yyyyMMdd}/{host}/{slug}.json</c> in the Logs
    /// bucket — the same save-everything envelope the harvest pages use. Guarded and swallowing: no R2
    /// service, or a write failure, must never fault the crawl.
    /// </summary>
    private static async Task SaveCrawlPageAsync(
        ActivityExecutionContext context, SiteCrawlState state, string url, string provider, string content, CancellationToken ct)
    {
        if (context.GetService<IR2StorageService>() is not { } r2)
        {
            return;
        }

        try
        {
            var now = context.GetService<ProfileOptions>()?.Now() ?? DateTimeOffset.UtcNow;
            var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "unknown";
            var slug = Slug(url);
            var key = $"crawl/{now:yyyyMMdd}/{host}/{slug}.json";
            var envelope = JsonSerializer.Serialize(new
            {
                url,
                fetchedAt = now,
                provider,
                searchId = state.SearchId,
                site = state.SiteUrl,
                chars = content.Length,
                content
            });
            await r2.StoreJsonAsync(envelope, key, R2Bucket.Logs, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // save-everything is best-effort — never fault the crawl over an archive write.
        }
    }

    /// <summary>Last 40 URL-safe characters of the URL, so the R2 key is stable and filesystem-safe.</summary>
    private static string Slug(string url)
    {
        var cleaned = new string(url.Where(char.IsLetterOrDigit).ToArray());
        if (cleaned.Length == 0)
        {
            return "page";
        }

        return cleaned.Length <= 40 ? cleaned : cleaned[^40..];
    }
}

/// <summary>The minimal slice of a rendered page the crawl activities need (content + provider) — decouples
/// them from the scraper's concrete <c>ScrapedPage</c> type.</summary>
internal sealed record ScrapedPageLike(string Content, string Provider);
