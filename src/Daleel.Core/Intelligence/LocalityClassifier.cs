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
        string? url, string countryCode, string? countryName = null, bool fromGeoTargetedSource = false,
        bool fromGeoScopedSearch = false, string? marketEvidence = null)
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

        // Generic-gTLD rule. Born as a US special case (amazon.com, walmart.com carry no "us"
        // signal at all), it proved just as true elsewhere: real Jordanian stores live on bare .com
        // too (jo-cell.com, dumyah.com, smartbuy-me.com) and were all dropped as "non-local" while
        // Google — queried WITH gl=jo — had already ranked them for the market. So when the result
        // came from a geo-SCOPED search (gl=cc — weaker than the geo-TARGETED shopping/places
        // short-circuit above) AND its own title/snippet mentions the market, a generic-gTLD host
        // counts as local unless the URL carries another country's signal (a foreign ccTLD is
        // already excluded by IsGenericGTld; a foreign locale path like "/uae-en/" is excluded
        // explicitly). The market-mention requirement is what keeps genuinely global sellers out:
        // AliExpress ranks under gl=jo too, but its snippets don't say "Jordan" — jo-cell's do.
        // The US keeps the rule unconditionally: its sellers are on .com even in un-scoped searches.
        if (IsGenericGTld(host) && !HasForeignLocalePath(path, cc))
        {
            if (cc == "us")
            {
                return true;
            }

            if (fromGeoScopedSearch && MentionsMarket(marketEvidence, cc, countryName))
            {
                return true;
            }
        }

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);

        // Host NAME carrying the country as a hyphen-delimited SEGMENT or the whole label
        // (jo-cell.com, cell-jo.com, jordan-store.com, store.jordan.com) — a seller that put the
        // country in its own domain is advertising its market. The country must be a bounded
        // segment, NOT a mid-word substring: "airjordan.com" (Nike) and "jordandental.com" (a US
        // dentist named Jordan) merely CONTAIN "jordan" and are not local.
        if (labels.Length >= 2)
        {
            var registrable = labels[^2];
            var name = countryName?.ToLowerInvariant();
            var segments = registrable.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(s => s == cc || (!string.IsNullOrWhiteSpace(name) && s == name)))
            {
                return true;
            }
        }

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
    /// True when free text (a result's title/snippet) names the market: the country name as a
    /// substring, or the country code as a standalone word ("JO Cell", "prices in JO"). City and
    /// currency mentions would strengthen this further; the country signals cover the observed
    /// cases without new inputs.
    /// </summary>
    private static bool MentionsMarket(string? text, string cc, string? countryName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(countryName) &&
            text.Contains(countryName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // cc as its own word: split on non-letters so "JO Cell" hits and "major" doesn't.
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || !char.IsLetter(text[i]))
            {
                if (i - start == cc.Length &&
                    string.Compare(text, start, cc, 0, cc.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return true;
                }
                start = i + 1;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a path carries ANOTHER market's locale/region segment (e.g. "/uae-en/" for a
    /// Jordan search, "/en-gb/" for a US one). Such a segment marks a generic-gTLD seller as
    /// foreign (noon.com/uae-en is a UAE store). Only the hyphenated locale form is treated as a
    /// signal — a bare segment is too ambiguous, and the target country's own "/jo/" path is
    /// matched by the locale loop in IsLocal regardless. Language tokens (en/ar) are never foreign.
    /// </summary>
    private static bool HasForeignLocalePath(string path, string cc)
    {
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = seg.Split('-');
            if (parts.Length != 2)
            {
                continue;
            }

            var localeShaped = parts.All(p => p.Length is 2 or 3 && p.All(char.IsLetter));
            if (localeShaped && parts.Any(p => p != cc && p is not ("en" or "ar")))
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
