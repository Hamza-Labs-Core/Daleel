using System.Globalization;
using System.Text.RegularExpressions;

namespace Daleel.Pipeline.Extraction;

/// <summary>
/// Pure derivation of a store's next-page URLs from a first listing-page URL, so a store crawl can walk
/// MULTIPLE product-listing pages instead of only the one Google returned. Handles the common paginable
/// shapes (<c>?page=N</c>, <c>?p=N</c>, <c>?start=N*perPage</c>, <c>/page/N</c>, <c>-page-N</c>) and falls
/// back to appending <c>?page=N</c>. No I/O — the caller fetches + extracts each derived URL and stops when
/// a page yields no new items (URL-paged sites only; JS/infinite-scroll stores need a different mechanism).
/// </summary>
public static class StorePagination
{
    /// <summary>
    /// Derives the URLs for pages 2..<paramref name="maxPages"/> from a first-page store URL. Returns empty
    /// when the URL isn't an absolute http(s) URL or <paramref name="maxPages"/> ≤ 1. Duplicates of the
    /// input (a no-op substitution) are dropped.
    /// </summary>
    public static IReadOnlyList<string> NextPages(string firstPageUrl, int maxPages, int perPage = 24)
    {
        if (maxPages <= 1 || string.IsNullOrWhiteSpace(firstPageUrl) ||
            !Uri.TryCreate(firstPageUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Array.Empty<string>();
        }

        var pages = new List<string>();
        for (var n = 2; n <= maxPages; n++)
        {
            var url = PageUrl(firstPageUrl, n, Math.Max(1, perPage));
            if (!string.Equals(url, firstPageUrl, StringComparison.OrdinalIgnoreCase))
            {
                pages.Add(url);
            }
        }

        return pages;
    }

    private static string PageUrl(string url, int page, int perPage)
    {
        var n = page.ToString(CultureInfo.InvariantCulture);

        if (Regex.IsMatch(url, @"[?&]page=\d+", RegexOptions.IgnoreCase))
        {
            return Regex.Replace(url, @"([?&]page=)\d+", "${1}" + n, RegexOptions.IgnoreCase);
        }

        if (Regex.IsMatch(url, @"[?&]p=\d+", RegexOptions.IgnoreCase))
        {
            return Regex.Replace(url, @"([?&]p=)\d+", "${1}" + n, RegexOptions.IgnoreCase);
        }

        if (Regex.IsMatch(url, @"[?&]start=\d+", RegexOptions.IgnoreCase))
        {
            var offset = ((page - 1) * perPage).ToString(CultureInfo.InvariantCulture);
            return Regex.Replace(url, @"([?&]start=)\d+", "${1}" + offset, RegexOptions.IgnoreCase);
        }

        if (Regex.IsMatch(url, @"/page/\d+", RegexOptions.IgnoreCase))
        {
            return Regex.Replace(url, @"(/page/)\d+", "${1}" + n, RegexOptions.IgnoreCase);
        }

        if (Regex.IsMatch(url, @"-page-\d+", RegexOptions.IgnoreCase))
        {
            return Regex.Replace(url, @"(-page-)\d+", "${1}" + n, RegexOptions.IgnoreCase);
        }

        // No recognised pagination marker — best-effort append (many stores accept ?page=N).
        return url + (url.Contains('?') ? "&" : "?") + "page=" + n;
    }
}
