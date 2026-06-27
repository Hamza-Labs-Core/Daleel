namespace Daleel.Web.Services;

/// <summary>
/// Display-time sanitization of a product's spec map. The deep-dive enrichment stows the RAW scraped page
/// text under a <c>details</c> key (and the model-research path can leave other long blobs) so the LLM has
/// something to read — but that raw content (store nav menus, category links, whole Arabic pages) must
/// NEVER reach the UI. This is the single chokepoint that keeps only clean, structured key/value pairs:
/// short, single-line, and free of links/markup. Detail dialogs and pages render <see cref="ForDisplay"/>,
/// never the raw <c>Specs</c> map.
/// </summary>
public static class ProductSpecs
{
    /// <summary>Keys that are carriers for raw scraped content, never real display specs.</summary>
    private static readonly HashSet<string> RawKeys =
        new(StringComparer.OrdinalIgnoreCase) { "details", "detail", "raw", "content", "page", "html", "markdown", "body", "text" };

    /// <summary>A genuine spec value is a short phrase; anything longer is a blob, not a value.</summary>
    private const int MaxValueLength = 200;

    /// <summary>The clean, structured key/value pairs safe to render, in original order.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ForDisplay(IReadOnlyDictionary<string, string>? specs)
    {
        if (specs is null || specs.Count == 0)
        {
            return Array.Empty<KeyValuePair<string, string>>();
        }

        return specs.Where(kv => IsCleanSpec(kv.Key, kv.Value)).ToList();
    }

    /// <summary>
    /// True only for a clean structured spec: a non-raw key with a short, single-line value that carries
    /// no links or markup. Everything else is treated as raw scrape spillover and dropped from the UI.
    /// </summary>
    public static bool IsCleanSpec(string? key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key) || RawKeys.Contains(key.Trim()))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var v = value.Trim();
        if (v.Length > MaxValueLength)
        {
            return false; // a blob (e.g. a scraped page), not a spec value
        }

        if (v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0)
        {
            return false; // multi-line → raw content
        }

        if (v.Contains("](", StringComparison.Ordinal) ||
            v.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            v.Contains("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false; // markdown/links → raw content
        }

        if (v.Contains('<') && v.Contains('>'))
        {
            return false; // stray HTML tags
        }

        return true;
    }
}
