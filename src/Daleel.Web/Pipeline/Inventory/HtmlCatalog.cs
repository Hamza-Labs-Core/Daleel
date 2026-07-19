using System.Xml.Linq;
using Daleel.Agent;
using Daleel.Search.Http;

namespace Daleel.Web.Pipeline.Inventory;

/// <summary>
/// Finds the LISTING/category pages of a store that exposes no machine-readable catalogue (neither
/// Shopify nor Woo JSON) — the entry points of the HTML inventory mode. Sitemap first (free, no LLM):
/// <c>sitemap.xml</c> and its index children are walked for category/collection URLs. Only when the
/// sitemap yields nothing does the discovery spend ONE LLM assess call on the store homepage's nav
/// (the store crawler's own prompt). Product URLs are never returned — the sync fans out one unit per
/// listing page, and category pages carry many products each.
/// </summary>
public interface IHtmlCatalogDiscovery
{
    /// <summary>Listing-page URLs on <paramref name="domain"/> (same-domain, deduped), or empty when
    /// the store exposes no discoverable catalogue. <paramref name="agent"/> null skips the LLM step.</summary>
    Task<IReadOnlyList<string>> DiscoverListingPagesAsync(
        string domain, AgentService? agent, CancellationToken ct = default);
}

/// <inheritdoc cref="IHtmlCatalogDiscovery"/>
public sealed class HtmlCatalogDiscovery : IHtmlCatalogDiscovery, IDisposable
{
    /// <summary>Loop-safety ceiling on child-sitemap fetches per walk (a sitemap INDEX can nest
    /// hundreds of files; this bounds the walk, it drops no discovered listing URLs).</summary>
    internal const int MaxChildSitemaps = 50;

    private readonly HttpClient _http;
    private readonly Daleel.Web.Services.IProviderApi? _providers;

    public HtmlCatalogDiscovery(Daleel.Web.Services.IProviderApi? providers = null, HttpClient? http = null)
    {
        _providers = providers;
        _http = http ?? SharedHttpHandler.CreateClient();
        if (_http.Timeout == default || _http.Timeout == TimeSpan.FromSeconds(100))
        {
            _http.Timeout = TimeSpan.FromSeconds(30);
        }
    }

    public async Task<IReadOnlyList<string>> DiscoverListingPagesAsync(
        string domain, AgentService? agent, CancellationToken ct = default)
    {
        var fromSitemap = await FromSitemapsAsync(domain, ct).ConfigureAwait(false);
        if (fromSitemap.Count > 0)
        {
            return fromSitemap;
        }

        return agent is null
            ? Array.Empty<string>()
            : await FromHomepageAsync(domain, agent, ct).ConfigureAwait(false);
    }

    /// <summary>Tries the conventional sitemap locations and walks the first one that parses.</summary>
    private async Task<IReadOnlyList<string>> FromSitemapsAsync(string domain, CancellationToken ct)
    {
        foreach (var entry in new[]
                 {
                     $"https://{domain}/sitemap.xml",
                     $"https://{domain}/sitemap_index.xml",
                     $"https://{domain}/wp-sitemap.xml"
                 })
        {
            var xml = await FetchTextAsync(entry, ct).ConfigureAwait(false);
            if (xml is null)
            {
                continue;
            }

            var found = await WalkSitemapAsync(xml, domain, ct).ConfigureAwait(false);
            if (found.Count > 0)
            {
                return found;
            }
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Walks one sitemap (index or urlset), fetching child sitemaps breadth-first up to the ceiling —
    /// category-looking children first, blog/media noise skipped — and collects every LISTING URL.
    /// </summary>
    private async Task<IReadOnlyList<string>> WalkSitemapAsync(string rootXml, string domain, CancellationToken ct)
    {
        var listings = new List<string>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedSitemaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        void Collect(SitemapDoc doc)
        {
            foreach (var url in doc.Urls)
            {
                if (CatalogUrlClassifier.IsListingUrl(url) && SameDomain(url, domain) && seenUrls.Add(url))
                {
                    listings.Add(url);
                }
            }

            foreach (var child in doc.Sitemaps
                         .Where(s => SameDomain(s, domain) && CatalogUrlClassifier.IsUsefulChildSitemap(s))
                         .OrderByDescending(CatalogUrlClassifier.LooksLikeCategorySitemap))
            {
                if (visitedSitemaps.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        Collect(SitemapXml.Parse(rootXml));

        var fetched = 0;
        while (queue.Count > 0 && fetched < MaxChildSitemaps && !ct.IsCancellationRequested)
        {
            var child = queue.Dequeue();
            fetched++;
            var xml = await FetchTextAsync(child, ct).ConfigureAwait(false);
            if (xml is not null)
            {
                Collect(SitemapXml.Parse(xml));
            }
        }

        return listings;
    }

    /// <summary>
    /// One LLM assess call over the store homepage's nav (the store crawler's own prompt) — the
    /// fallback for stores whose sitemap exposes no category pages. Returns the assessment's listing
    /// URLs; when it points at a sitemap instead, that sitemap is walked too.
    /// </summary>
    private async Task<IReadOnlyList<string>> FromHomepageAsync(
        string domain, AgentService agent, CancellationToken ct)
    {
        var home = $"https://{domain}/";
        var content = (await agent.ReadPageAsync(home, ct).ConfigureAwait(false))?.Content;
        if (string.IsNullOrWhiteSpace(content) && _providers is not null)
        {
            content = (await ScrapeViaProvidersAsync(home, ct).ConfigureAwait(false))?.Content;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<string>();
        }

        var assessment = await agent.AssessStoreAsync(
            home, content, "the store's entire product catalogue (full inventory sync)", ct).ConfigureAwait(false);

        var listings = assessment.ListingUrls
            .Where(u => SameDomain(u, domain))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (listings.Count > 0)
        {
            return listings;
        }

        if (assessment.SitemapUrl is { Length: > 0 } sitemap && SameDomain(sitemap, domain) &&
            await FetchTextAsync(sitemap, ct).ConfigureAwait(false) is { } xml)
        {
            return await WalkSitemapAsync(xml, domain, ct).ConfigureAwait(false);
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Fetches a text resource (sitemap XML) — direct first (cheapest), then through the provider
    /// chain per the every-fetch-path invariant (a datacenter IP is often bot-walled where a rendered
    /// browser isn't). SSRF-guarded: sitemap-discovered URLs are untrusted input.
    /// </summary>
    private async Task<string?> FetchTextAsync(string url, CancellationToken ct)
    {
        if (!SsrfGuard.IsSafePublicUrl(url))
        {
            return null;
        }

        try
        {
            using var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (body.Contains('<'))
                {
                    return body;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // fall through to the provider chain
        }

        return (await ScrapeViaProvidersAsync(url, ct).ConfigureAwait(false))?.Content;
    }

    private async Task<Daleel.Search.Abstractions.ScrapedPage?> ScrapeViaProvidersAsync(
        string url, CancellationToken ct)
    {
        if (_providers is null)
        {
            return null;
        }

        try
        {
            return await _providers.ScrapePageAsync(url, Daleel.Search.Abstractions.ScrapeFormat.Html, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="url"/>'s host is the domain itself or a subdomain of it.</summary>
    internal static bool SameDomain(string url, string domain)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return false;
        }

        var host = u.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        var bare = domain.ToLowerInvariant();
        return host == bare || host.EndsWith("." + bare, StringComparison.Ordinal);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>One parsed sitemap document: child sitemap locations (index files) + page URLs (urlsets).</summary>
public sealed record SitemapDoc(IReadOnlyList<string> Sitemaps, IReadOnlyList<string> Urls)
{
    public static readonly SitemapDoc Empty = new(Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>Tolerant sitemap XML reader — namespace-agnostic, empty on unparseable input.</summary>
public static class SitemapXml
{
    /// <summary>Reads <c>&lt;sitemapindex&gt;</c> and <c>&lt;urlset&gt;</c> documents. A browser-rendered
    /// fetch may wrap the XML in a shell, so parsing starts at the first angle bracket. Never throws.</summary>
    public static SitemapDoc Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return SitemapDoc.Empty;
        }

        try
        {
            var start = xml.IndexOf('<');
            var doc = XDocument.Parse(start > 0 ? xml[start..] : xml);
            var sitemaps = new List<string>();
            var urls = new List<string>();
            foreach (var loc in doc.Descendants().Where(e => e.Name.LocalName == "loc"))
            {
                var value = loc.Value.Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                switch (loc.Parent?.Name.LocalName)
                {
                    case "sitemap":
                        sitemaps.Add(value);
                        break;
                    case "url":
                        urls.Add(value);
                        break;
                }
            }

            return new SitemapDoc(sitemaps, urls);
        }
        catch
        {
            return SitemapDoc.Empty;
        }
    }
}

/// <summary>
/// Classifies catalogue URLs by SHAPE: a LISTING (category/collection) page carries many products —
/// the unit of the HTML-mode fan-out — while a PRODUCT page carries one and is never fanned out.
/// Substring markers over the path, ordered so Woo's <c>/product-category/</c> (a listing) is not
/// mistaken for <c>/product/</c> (a product).
/// </summary>
public static class CatalogUrlClassifier
{
    private static readonly string[] ListingMarkers =
    {
        "/product-category/", "/product_cat/", "/collections/", "/collection/",
        "/categories/", "/category/", "/shop/", "/c/"
    };

    private static readonly string[] ProductMarkers =
    {
        "/products/", "/product/", "/item/", "/items/", "/p/"
    };

    /// <summary>Sitemap children that never contain catalogue pages (blog/media/author noise).</summary>
    private static readonly string[] NoiseSitemapMarkers =
    {
        "blog", "post-sitemap", "post_tag", "page-sitemap", "image", "video", "news", "author", "tag-sitemap"
    };

    public static bool IsProductUrl(string url)
    {
        var path = PathOf(url);
        return ProductMarkers.Any(m => path.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsListingUrl(string url)
    {
        var path = PathOf(url);
        return ListingMarkers.Any(m => path.Contains(m, StringComparison.OrdinalIgnoreCase)) &&
               !ProductMarkers.Any(m => path.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>False for sitemap children that only ever hold blog/media URLs.</summary>
    public static bool IsUsefulChildSitemap(string url) =>
        !NoiseSitemapMarkers.Any(m => url.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>True for children that likely hold the category pages — fetched first under the ceiling.</summary>
    public static bool LooksLikeCategorySitemap(string url) =>
        url.Contains("cat", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("collection", StringComparison.OrdinalIgnoreCase);

    private static string PathOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? u.AbsolutePath.EndsWith('/') ? u.AbsolutePath : u.AbsolutePath + "/"
            : url;
}
