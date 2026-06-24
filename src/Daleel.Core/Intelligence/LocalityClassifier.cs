namespace Daleel.Core.Intelligence;

/// <summary>
/// Decides whether a result is <em>local</em> to a target market — i.e. something the user
/// can actually buy from in-country — using only generic, geo-agnostic signals so it works
/// for any country without a hardcoded source list.
/// </summary>
/// <remarks>
/// Local signals, any of which is sufficient:
/// <list type="bullet">
///   <item>The result came from a geo-targeted source (Google Shopping with <c>gl=cc</c>, or
///   Google Places) — the search itself constrained it to the market.</item>
///   <item>A country TLD (e.g. <c>store.com.jo</c> or <c>x.jo</c>).</item>
///   <item>A country subdomain (e.g. <c>jo.example.com</c>).</item>
///   <item>A country path/locale segment (e.g. <c>/jo/</c>, <c>/en-jo/</c>, <c>/jordan/</c>).</item>
/// </list>
/// Everything else is treated as non-local. The policy of what to do with non-local results
/// (drop them, unless the user asked for international) lives in the caller.
/// </remarks>
public static class LocalityClassifier
{
    /// <summary>
    /// True when a result is confirmed local to <paramref name="countryCode"/> (ISO alpha-2,
    /// e.g. "jo"). <paramref name="fromGeoTargetedSource"/> short-circuits to local for hits
    /// that came from a geo-constrained provider (shopping/places).
    /// </summary>
    public static bool IsLocal(
        string? url, string countryCode, string? countryName = null, bool fromGeoTargetedSource = false)
    {
        if (fromGeoTargetedSource)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(countryCode) || string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return false;
        }

        var cc = countryCode.Trim().ToLowerInvariant();
        var host = u.Host.ToLowerInvariant();
        var path = u.AbsolutePath.ToLowerInvariant();

        // Country TLD: host ends with ".{cc}".
        if (host.EndsWith("." + cc, StringComparison.Ordinal))
        {
            return true;
        }

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);

        // Country subdomain: the left-most host label is the country code (e.g. jo.example.com).
        if (labels.Length > 2 && labels[0] == cc)
        {
            return true;
        }

        // Country / locale path segment: /jo/, /en-jo/, /jo-en/, /jordan/.
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == cc || seg.EndsWith("-" + cc, StringComparison.Ordinal) ||
                seg.StartsWith(cc + "-", StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(countryName) &&
                seg.Equals(countryName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Heuristically detects whether the user explicitly asked to include international /
    /// non-local sources (e.g. "show international options too", "worldwide", "عالمي").
    /// </summary>
    public static bool QueryWantsInternational(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        var q = query.ToLowerInvariant();
        string[] markers =
        {
            "international", "worldwide", "global", "overseas", "abroad", "imported", "import ",
            "ship to", "ships to", "عالمي", "دولي", "خارج", "استيراد"
        };
        return markers.Any(m => q.Contains(m, StringComparison.Ordinal));
    }
}
