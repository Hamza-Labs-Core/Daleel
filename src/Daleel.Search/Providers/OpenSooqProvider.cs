using System.Text.RegularExpressions;
using Daleel.Core.Models;
using Daleel.Core.Pricing;
using Daleel.Search.Abstractions;

namespace Daleel.Search.Providers;

/// <summary>
/// Reads listings from <see href="https://opensooq.com">OpenSooq</see>, the largest
/// classifieds site across Jordan and the Gulf. OpenSooq is JS-rendered and aggressively
/// anti-bot, so this provider does not fetch HTTP directly — it routes the search URL
/// through an <see cref="IScrapeProvider"/> (Context.dev / Cloudflare Browser) to get
/// clean markdown, then extracts listings heuristically.
/// </summary>
public sealed partial class OpenSooqProvider
{
    private readonly IScrapeProvider _scraper;
    private readonly string _defaultCurrency;

    public string Name => "opensooq";

    public OpenSooqProvider(IScrapeProvider scraper, string defaultCurrency = "JOD")
    {
        _scraper = scraper ?? throw new ArgumentNullException(nameof(scraper));
        _defaultCurrency = defaultCurrency;
    }

    // Markdown links look like: [Listing title](https://jo.opensooq.com/en/...)
    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^\)]*opensooq[^\)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ListingLinkRegex();

    /// <summary>
    /// Searches OpenSooq for <paramref name="query"/> in a given country and returns the
    /// extracted listings as price points.
    /// </summary>
    public async Task<IReadOnlyList<PricePoint>> SearchAsync(
        string query,
        string countryCode = "jo",
        CancellationToken cancellationToken = default)
    {
        var url = BuildSearchUrl(query, countryCode);
        var page = await _scraper.ScrapeAsync(url, ScrapeFormat.Markdown, cancellationToken).ConfigureAwait(false);

        if (!page.Success || string.IsNullOrWhiteSpace(page.Content))
        {
            return Array.Empty<PricePoint>();
        }

        return Extract(page.Content, url);
    }

    /// <summary>Builds the OpenSooq search URL for a country subdomain.</summary>
    public static string BuildSearchUrl(string query, string countryCode)
    {
        var sub = countryCode.ToLowerInvariant() switch
        {
            "jo" => "jo",
            "sa" => "sa",
            "ae" => "ae",
            "eg" => "eg",
            _ => "jo"
        };
        return $"https://{sub}.opensooq.com/en/search?term={Uri.EscapeDataString(query)}";
    }

    /// <summary>
    /// Extracts listings from scraped markdown. Each markdown listing link becomes a
    /// candidate; a price is pulled from the same line when present.
    /// </summary>
    internal IReadOnlyList<PricePoint> Extract(string markdown, string sourceUrl)
    {
        var results = new List<PricePoint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in markdown.Split('\n'))
        {
            var match = ListingLinkRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var title = match.Groups[1].Value.Trim();
            var url = match.Groups[2].Value.Trim();
            if (title.Length == 0 || !seen.Add(url))
            {
                continue;
            }

            // Parse the price from the text AFTER the markdown link only — the title can
            // contain numbers (e.g. "24000 BTU") that would otherwise be mistaken for a price.
            var afterLink = line[(match.Index + match.Length)..];
            Money? price = null;
            if (PriceParser.TryParse(afterLink, out var m, _defaultCurrency))
            {
                price = m;
            }

            results.Add(new PricePoint
            {
                Product = title,
                Price = price ?? new Money(0, _defaultCurrency),
                Store = "OpenSooq",
                Url = url
            });
        }

        return results;
    }
}
