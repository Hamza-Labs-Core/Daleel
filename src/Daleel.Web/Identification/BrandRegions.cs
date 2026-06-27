namespace Daleel.Web.Identification;

/// <summary>One market the brand-catalogue searcher looks for a brand's regional site in.</summary>
public sealed record BrandRegion(string Key, string Name);

/// <summary>
/// Expands a brand's base domain into the regional site candidates worth crawling. In Jordan a store's
/// model name is often the local SKU, so a product can only be matched by also pulling the brand's
/// in-region catalogue — but regional URL conventions are inconsistent (<c>samsung.com/jo</c>,
/// <c>samsung.jo</c>, <c>samsung.com.jo</c>, <c>lg.com/levant</c>). Rather than guess one pattern, this
/// produces a <em>ranked</em> candidate list (Jordan-first, then the wider GCC, then global) that the
/// searcher probes best-effort and stops on the first that yields models.
/// </summary>
public static class BrandRegions
{
    /// <summary>The markets we search, in priority order — Jordan first (it's the home market).</summary>
    public static readonly IReadOnlyList<BrandRegion> All = new[]
    {
        new BrandRegion("jordan", "Jordan"),
        new BrandRegion("uae", "UAE"),
        new BrandRegion("saudi", "Saudi Arabia"),
        new BrandRegion("egypt", "Egypt"),
        new BrandRegion("global", "Global"),
    };

    /// <summary>Per-region ccTLD labels and URL path segments used to synthesize candidate sites.</summary>
    private static readonly IReadOnlyDictionary<string, (string[] CcTlds, string[] Paths)> RegionPatterns =
        new Dictionary<string, (string[], string[])>
        {
            ["jordan"] = (new[] { "jo", "com.jo" }, new[] { "jo", "en-jo", "ar-jo", "levant" }),
            ["uae"] = (new[] { "ae", "com.ae" }, new[] { "ae", "en-ae" }),
            ["saudi"] = (new[] { "sa", "com.sa" }, new[] { "sa", "en-sa" }),
            ["egypt"] = (new[] { "eg", "com.eg" }, new[] { "eg", "en-eg" }),
            ["global"] = (Array.Empty<string>(), new[] { "global", "int" }),
        };

    /// <summary>
    /// Brand-specific regional patterns we actually know, keyed by the brand's domain stem (first label).
    /// These take precedence over the generic synthesis so well-known brands are probed correctly first.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> KnownBrands =
        new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["samsung"] = new Dictionary<string, string[]>
            {
                ["jordan"] = new[] { "samsung.com/levant", "samsung.com/jo" },
                ["uae"] = new[] { "samsung.com/ae" },
                ["saudi"] = new[] { "samsung.com/sa" },
                ["egypt"] = new[] { "samsung.com/eg" },
                ["global"] = new[] { "samsung.com" },
            },
            ["lg"] = new Dictionary<string, string[]>
            {
                ["jordan"] = new[] { "lg.com/levant_en", "lg.com/levant" },
                ["uae"] = new[] { "lg.com/ae" },
                ["saudi"] = new[] { "lg.com/sa_en", "lg.com/sa" },
                ["egypt"] = new[] { "lg.com/eg" },
                ["global"] = new[] { "lg.com" },
            },
        };

    /// <summary>
    /// Ranked candidate site identifiers (domain or domain/path) for a base domain, de-duplicated and
    /// Jordan-first. The base domain itself is always included (under its best-fit region). <paramref
    /// name="maxPerRegion"/> caps how many synthesized variants each region contributes.
    /// </summary>
    public static IReadOnlyList<(BrandRegion Region, string Domain)> CandidatesFor(string? baseDomain, int maxPerRegion = 2)
    {
        var result = new List<(BrandRegion, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var root = RootDomain(baseDomain);
        if (root is null)
        {
            return result;
        }

        var stem = root.Split('.')[0];
        KnownBrands.TryGetValue(stem, out var known);

        foreach (var region in All)
        {
            var added = 0;

            // 1. Brand-specific known patterns win.
            if (known is not null && known.TryGetValue(region.Key, out var knownDomains))
            {
                foreach (var d in knownDomains)
                {
                    if (added >= maxPerRegion) break;
                    if (seen.Add(d))
                    {
                        result.Add((region, d));
                        added++;
                    }
                }
            }

            if (added >= maxPerRegion)
            {
                continue;
            }

            // 2. Generic synthesis: ccTLD forms (samsung.jo), then path forms (samsung.com/jo).
            var (ccTlds, paths) = RegionPatterns[region.Key];
            foreach (var cc in ccTlds)
            {
                if (added >= maxPerRegion) break;
                var d = $"{stem}.{cc}";
                if (seen.Add(d))
                {
                    result.Add((region, d));
                    added++;
                }
            }

            foreach (var path in paths)
            {
                if (added >= maxPerRegion) break;
                var d = $"{root}/{path}";
                if (seen.Add(d))
                {
                    result.Add((region, d));
                    added++;
                }
            }

            // 3. The global region always includes the bare base domain.
            if (region.Key == "global" && seen.Add(root))
            {
                result.Add((region, root));
            }
        }

        return result;
    }

    /// <summary>Reduces a URL or host to its registrable-ish root domain (strips scheme, "www.", path).</summary>
    internal static string? RootDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var s = url.Trim();
        if (!s.Contains("://", StringComparison.Ordinal))
        {
            s = "https://" + s;
        }

        if (!Uri.TryCreate(s, UriKind.Absolute, out var u))
        {
            return null;
        }

        var host = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
        return string.IsNullOrWhiteSpace(host) ? null : host.ToLowerInvariant();
    }
}
