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

        // US special case: unlike the Arab markets (where local sellers carry a ".jo"/".ae" ccTLD,
        // a "jo." subdomain, or a "/jordan/" path), the US's de-facto local commerce namespace is the
        // bare generic gTLD — amazon.com, walmart.com, rei.com, away.com all live on plain .com with no
        // "us" signal at all. Without this, every US retailer fails the country checks below and is
        // dropped as "non-local", so a "best carry on USA" search keeps only thin geo-targeted shopping
        // hits and returns no real products or brands. So for the US, a generic-gTLD host counts as
        // local UNLESS the URL carries another country's signal (a foreign ccTLD is already excluded by
        // IsGenericGTld; a foreign locale path like "/uae-en/" is excluded explicitly).
        if (cc == "us" && IsGenericGTld(host) && !HasForeignLocalePath(path))
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
    /// Generic, country-agnostic top-level domains — the namespace US sellers use (no country ccTLD).
    /// Includes the common 2-letter TLDs (.co/.io/.ai/.tv/.me) that, despite being technical ccTLDs,
    /// are used generically by US brands. A foreign ccTLD (.ae, .uk, .jo) is deliberately absent, so a
    /// genuinely foreign seller still fails the US locality check.
    /// </summary>
    private static readonly HashSet<string> GenericGTlds = new(StringComparer.Ordinal)
    {
        "com", "net", "org", "store", "shop", "shopping", "info", "biz",
        "us", "co", "io", "ai", "tv", "me", "app", "dev",
    };

    /// <summary>True when the host's right-most label is a generic gTLD (e.g. amazon.com → "com").</summary>
    private static bool IsGenericGTld(string host)
    {
        var lastDot = host.LastIndexOf('.');
        if (lastDot < 0 || lastDot == host.Length - 1)
        {
            return false;
        }

        return GenericGTlds.Contains(host[(lastDot + 1)..]);
    }

    /// <summary>
    /// True when a path carries a non-US locale/region segment (e.g. "/uae-en/", "/en-gb/"). Such a
    /// segment marks a generic-gTLD seller as foreign even for the US (noon.com/uae-en is a UAE store).
    /// Only the hyphenated locale form is treated as a signal — a bare segment is too ambiguous, and
    /// the target country's own "/us/" path is matched by the locale loop below regardless.
    /// </summary>
    private static bool HasForeignLocalePath(string path)
    {
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = seg.Split('-');
            if (parts.Length != 2)
            {
                continue;
            }

            // Locale-shaped "xx-yy" / "uae-en" where each part is a 2–3 letter alpha token, and it
            // names neither the US nor plain English → a foreign market locale.
            var localeShaped = parts.All(p => p.Length is 2 or 3 && p.All(char.IsLetter));
            if (localeShaped && parts.Any(p => p is not ("us" or "en")))
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
