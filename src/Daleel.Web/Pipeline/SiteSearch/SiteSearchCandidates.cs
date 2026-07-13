using System.Text.RegularExpressions;
using Daleel.Web.Data;

namespace Daleel.Web.Pipeline.SiteSearch;

/// <summary>
/// The ordered search-URL candidates for one store domain: the LEARNED per-domain template first
/// (see <see cref="SiteSearchProfile"/>), then the known platform conventions. This replaces the old
/// single hardcoded Shopify-style <c>/search?q=</c> guess — the gate audit's biggest finding: Jordan's
/// stores are mostly WooCommerce (<c>/?s=</c>) or custom platforms, so that one guess 404'd nearly
/// every store and left taghareedstore (Shopify) as the only item source.
/// </summary>
public static class SiteSearchCandidates
{
    /// <summary>The known platform conventions, tried in order when nothing has been learned yet.</summary>
    private static readonly string[] Conventions =
    {
        "{root}/search?q={query}", // Shopify (and several custom engines that alias it)
        "{root}/?s={query}"        // WordPress/WooCommerce
    };

    public static IReadOnlyList<string> For(string domain, string query, SiteSearchProfile? profile)
    {
        var root = domain.Contains("://", StringComparison.Ordinal)
            ? domain.TrimEnd('/')
            : $"https://{domain.TrimEnd('/')}";
        if (string.IsNullOrWhiteSpace(query))
        {
            return new[] { root }; // no query to type into a search box — the root is all there is
        }

        var escaped = Uri.EscapeDataString(query);
        var urls = new List<string>();

        // The learned winner first: it produced extractable products before, so it goes ahead of guesses.
        if (!string.IsNullOrWhiteSpace(profile?.SearchUrlTemplate) &&
            profile!.SearchUrlTemplate.Contains("{query}", StringComparison.Ordinal))
        {
            urls.Add(profile.SearchUrlTemplate.Replace("{query}", escaped, StringComparison.Ordinal));
        }

        foreach (var convention in Conventions)
        {
            var url = convention
                .Replace("{root}", root, StringComparison.Ordinal)
                .Replace("{query}", escaped, StringComparison.Ordinal);
            if (!urls.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                urls.Add(url);
            }
        }

        return urls;
    }

    /// <summary>
    /// The reusable template of a winning URL — the concrete query swapped back to <c>{query}</c> —
    /// or null when the URL doesn't look like a parameterized search (nothing to learn from a root hit).
    /// </summary>
    public static string? TemplateFor(string url)
    {
        // The query value is whatever follows the last '=': all candidates put the query there.
        var eq = url.LastIndexOf('=');
        if (eq < 0 || eq == url.Length - 1)
        {
            return null;
        }

        return url[..(eq + 1)] + "{query}";
    }
}

/// <summary>
/// Judges whether a fetched harvest page is worth extracting from, or is an error/no-results shell the
/// harvest should skip in favor of the next candidate URL. The old code extracted whatever came back —
/// including HTTP-200 soft-404s ("404 - تسوّق من ركن بغداد") and "No results found" pages — and recorded
/// the store as an inexplicable EmptyCrawl.
/// </summary>
public static class HarvestPageJudge
{
    /// <summary>A page shorter than this can't hold even one product listing — a JS skeleton or stub.</summary>
    private const int MinUsableChars = 80;

    /// <summary>Error/soft-404 markers, judged near the TOP of the page (title region), where they are
    /// page-level verdicts rather than incidental body text.</summary>
    private static readonly Regex ErrorHead = new(
        @"\b404\b|page not found|nothing found|الصفحة غير موجودة",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Explicit empty-result verdicts, anywhere: these phrases only appear as the answer.</summary>
    private static readonly Regex NoResults = new(
        @"no (results|products) (were )?found|\b0 results\b|لا توجد نتائج|لم يتم العثور",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsUsable(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length < MinUsableChars)
        {
            return false;
        }

        var head = content.Length <= 300 ? content : content[..300];
        return !ErrorHead.IsMatch(head) && !NoResults.IsMatch(content);
    }
}
