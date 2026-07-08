using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Daleel.Core.Arabic;
using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Search.Abstractions;

namespace Daleel.Pipeline.Extraction;

/// <summary>
/// Turns marketplace/store pages and shopping-API hits into structured
/// <see cref="ProductListing"/>s, then merges and de-duplicates them across sources.
/// </summary>
/// <remarks>
/// All the parsing/merging logic is pure and static so it is fully unit-testable against
/// fixture JSON (the "mock HTML" path) and fake <see cref="SearchResult"/>s. The only
/// I/O — calling Context.dev's AI Extract — is the thin <see cref="ExtractAsync"/> wrapper
/// around an injected <see cref="IExtractProvider"/>.
/// </remarks>
public static class ListingExtractor
{
    /// <summary>
    /// The JSON Schema handed to Context.dev's AI Extract. Describes the product fields we
    /// want pulled out of an arbitrary marketplace/store page.
    /// </summary>
    public static readonly object ListingSchema = new
    {
        type = "object",
        properties = new
        {
            products = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string" },
                        brand = new { type = "string" },
                        model = new { type = "string" },
                        price = new { type = "number" },
                        currency = new { type = "string" },
                        url = new { type = "string" },
                        image_url = new { type = "string" },
                        specs = new { type = "object" },
                        availability = new { type = "string" },
                        seller = new { type = "string" },
                        condition = new { type = "string" },
                        reviews = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    text = new { type = "string" },
                                    rating = new { type = "number" },
                                    author = new { type = "string" }
                                }
                            }
                        }
                    }
                }
            }
        }
    };

    /// <summary>
    /// Runs Context.dev AI Extract on a page and parses the result into listings. Returns
    /// an empty list on any extraction failure (failure-isolated, like the rest of gather).
    /// </summary>
    public static async Task<IReadOnlyList<ProductListing>> ExtractAsync(
        IExtractProvider extractor,
        string url,
        string source,
        ResultType sourceType,
        string? defaultCurrency = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await extractor.ExtractAsync(url, ListingSchema, cancellationToken).ConfigureAwait(false);
            return FromExtractedJson(json, source, sourceType, defaultCurrency);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return Array.Empty<ProductListing>();
        }
    }

    /// <summary>
    /// Parses a Context.dev extraction result (the <c>{ "products": [...] }</c> shape) into
    /// listings. Tolerant of missing fields and of the array being returned bare (without
    /// the <c>products</c> wrapper).
    /// </summary>
    public static IReadOnlyList<ProductListing> FromExtractedJson(
        JsonElement root, string source, ResultType sourceType, string? defaultCurrency = null)
    {
        var array = ResolveProductsArray(root);
        if (array is not { ValueKind: JsonValueKind.Array })
        {
            return Array.Empty<ProductListing>();
        }

        var listings = new List<ProductListing>();
        foreach (var item in array.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var rawName = Str(item, "name", "title");
            var model = Str(item, "model");
            var price = Num(item, "price");
            var url = Str(item, "url", "link");

            // Clean the raw name down to a real product name: unwrap markdown product cards (store
            // category pages scraped via CF Browser dump "[badge **Name** price sizes](url)" into the
            // name field), reject URL-shaped names and search/category page titles, and fall back to the
            // model number when the name field is unusable — mirroring the LLM path's PickName. This is
            // what lets colour-variant listings share a dedup key so the aggregator collapses them.
            var name = CleanExtractedName(rawName) ?? CleanExtractedName(model);

            // A row with no usable name (missing, only a URL, or a category page title) is not a
            // product — skip it rather than render a nameless/garbage card in the grid.
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            // A row whose link is a search/category LISTING page is a crawl entry point, not an
            // individual product (user report on "women pants": "some lists are store search pages
            // and not individual items"). Real product urls (e.g. /p/…/CS231) are kept.
            if (IsListingPageUrl(url))
            {
                continue;
            }

            listings.Add(new ProductListing
            {
                Name = name,
                Brand = Str(item, "brand"),
                Model = model,
                Price = price,
                Currency = Str(item, "currency") ?? (price is not null ? defaultCurrency : null),
                Url = url,
                ImageUrl = Str(item, "image_url", "imageUrl", "image"),
                Source = source,
                SourceType = sourceType,
                Specs = Specs(item),
                Availability = Str(item, "availability"),
                Condition = NormalizeCondition(Str(item, "condition")),
                OriginalPrice = Num(item, "original_price", "originalPrice", "was_price"),
                Seller = Str(item, "seller"),
                RatedReviews = ParseReviews(item)
            });
        }

        return listings;
    }

    /// <summary>Parses a listing's optional "reviews" array — buyer reviews scraped from the product page.
    /// Capped at 20 (review-spam guard); a review with no text is skipped.</summary>
    private static IReadOnlyList<ProductReview> ParseReviews(JsonElement item)
    {
        if (!item.TryGetProperty("reviews", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ProductReview>();
        }

        var reviews = new List<ProductReview>();
        foreach (var r in arr.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var text = Str(r, "text", "review", "body", "content");
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var rating = Num(r, "rating", "stars", "score");
            reviews.Add(new ProductReview(text!, rating is { } n ? (double)n : null, Str(r, "author", "name")));
        }

        return reviews.Take(20).ToList();
    }

    /// <summary>
    /// Maps already-structured shopping hits (SerpAPI <c>google_shopping</c>) into listings.
    /// </summary>
    public static IReadOnlyList<ProductListing> FromShopping(
        IEnumerable<SearchResult> shopping, ResultType sourceType = ResultType.Marketplace)
    {
        var listings = new List<ProductListing>();
        foreach (var r in shopping)
        {
            if (string.IsNullOrWhiteSpace(r.Title) && r.Price is null)
            {
                continue;
            }

            // A shopping hit whose "title" is a URL/bare domain ("atelier21.org") has no product
            // identity: it can't dedup-merge (the key is the normalized name) and would surface a
            // raw link as a product card. Same guard as the extracted-JSON path above.
            if (!string.IsNullOrWhiteSpace(r.Title) && LooksLikeUrl(r.Title))
            {
                continue;
            }

            listings.Add(new ProductListing
            {
                Name = r.Title,
                Brand = GuessBrand(r.Title),
                Price = r.Price?.Amount,
                Currency = r.Price?.Currency,
                Url = r.Url,
                Source = r.Seller ?? r.Source,
                Seller = r.Seller,
                ImageUrl = r.ImageUrl,
                Rating = r.Rating,
                RatingCount = r.ReviewCount,
                SourceType = sourceType
            });
        }

        return listings;
    }

    /// <summary>
    /// Merges listings from several sources, dropping duplicates that share a model (or, when
    /// no model is known, a normalized name). The first occurrence of a key wins, so callers
    /// should pass their most-trusted/most-complete source first.
    /// </summary>
    public static IReadOnlyList<ProductListing> Merge(params IEnumerable<ProductListing>[] sources)
    {
        var merged = new List<ProductListing>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            foreach (var listing in source)
            {
                if (seen.Add(DedupKey(listing)))
                {
                    merged.Add(listing);
                }
            }
        }

        return merged;
    }

    /// <summary>
    /// The de-duplication key for a listing: brand+model when a model is present (the
    /// strongest identity), otherwise the normalized product name.
    /// </summary>
    public static string DedupKey(ProductListing listing)
    {
        if (!string.IsNullOrWhiteSpace(listing.Model))
        {
            // Normalize Arabic orthography then case-fold (the normalizer leaves Latin case as-is),
            // so "AR24TXHQ" and "ar24txhq" collapse to one model key.
            var brand = ArabicNormalizer.Normalize(listing.Brand ?? string.Empty).ToLowerInvariant();
            var model = ArabicNormalizer.Normalize(listing.Model!).Replace(" ", string.Empty).ToLowerInvariant();
            return $"m:{brand}|{model}";
        }

        return $"n:{ArabicNormalizer.Normalize(listing.Name).ToLowerInvariant()}";
    }

    private static JsonElement? ResolveProductsArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "products", "items", "listings", "results" })
            {
                if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    return arr;
                }
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> Specs(JsonElement item)
    {
        if (!item.TryGetProperty("specs", out var specs) || specs.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>();
        }

        var dict = new Dictionary<string, string>();
        foreach (var prop in specs.EnumerateObject())
        {
            var value = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(value))
            {
                dict[prop.Name] = value!;
            }
        }

        return dict;
    }

    private static string? Str(JsonElement item, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s.Trim();
                }
            }
        }

        return null;
    }

    private static decimal? Num(JsonElement item, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!item.TryGetProperty(key, out var v))
            {
                continue;
            }

            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d))
            {
                return d;
            }

            // Prices often arrive as strings like "450 JOD" or "$1,299.00".
            if (v.ValueKind == JsonValueKind.String)
            {
                var digits = new string(v.GetString()!.Where(c => char.IsDigit(c) || c is '.' or ',').ToArray())
                    .Replace(",", string.Empty);
                if (decimal.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Canonicalizes a free-text condition into one of "new"/"used"/"refurbished" (Arabic and
    /// English aware), else the trimmed original. Shared so LLM-extracted offers normalize the
    /// same way as the deterministic parsers. Returns null for blank input.
    /// </summary>
    public static string? NormalizeCondition(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var s = raw.ToLowerInvariant();
        if (s.Contains("refurb") || s.Contains("مجدد")) return "refurbished";
        if (s.Contains("used") || s.Contains("مستعمل") || s.Contains("second")) return "used";
        if (s.Contains("new") || s.Contains("جديد")) return "new";
        return raw.Trim();
    }

    // A URL / bare domain sometimes lands in the name field instead of the product name:
    // "https://x.com/p", "www.foo.io", "amazon.com/dp/B0…". The final label must be all letters (a TLD)
    // so real model numbers like "GX-3.5" or "A2.1" are NOT mistaken for domains. Mirrors the guard in
    // AgentService.Products so the deterministic and LLM extraction paths reject links the same way.
    private static readonly Regex UrlLikeName = new(
        @"^\s*(https?://|www\.)|^\s*([a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,24}(/\S*)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool LooksLikeUrl(string text) => UrlLikeName.IsMatch(text.Trim());

    // A scraped store card arrives as ONE markdown link [anchor text](url) whose anchor text is the
    // whole card (badge + **name** + price + sizes). Anchored to the end so the last "](url)" wins.
    private static readonly Regex MarkdownLink = new(
        @"^\s*\[(?<text>.*)\]\((?<url>[^)]*)\)\s*$",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Inside a card, the product name is the BOLD run (**Cosmo pant in linen** / __name__).
    private static readonly Regex BoldSegment = new(
        @"\*\*(?<b1>.+?)\*\*|__(?<b2>.+?)__",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Leftover markdown emphasis markers and hard-break backslashes.
    private static readonly Regex MarkdownNoise = new(@"\*\*|__|\*|\\+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    // A genuine product name is short; anything longer is a scraped multi-field blob, not a name.
    private const int MaxNameLength = 120;

    /// <summary>
    /// Turns a raw extracted "name" into a clean product name, or null when it is not a product name at
    /// all. Handles the three ways extractors corrupt the field: (1) a full markdown product card
    /// <c>[badge **Name** $price sizes](url)</c> — unwrap the link, prefer the bolded run; (2) a source
    /// URL / bare domain dropped into the name; (3) a search/category PAGE TITLE masquerading as an item.
    /// Shared by the deterministic (<see cref="FromExtractedJson"/>) and LLM extraction paths so both
    /// reject the same noise. Returning null means "drop this row".
    /// </summary>
    public static string? CleanExtractedName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var s = raw.Trim();

        // 1) Unwrap a markdown link down to its anchor text (the scraped card blob).
        var link = MarkdownLink.Match(s);
        if (link.Success)
        {
            s = link.Groups["text"].Value;
        }

        // 2) Prefer the bolded run — the real product name amid badge/price/size noise.
        var bold = BoldSegment.Match(s);
        if (bold.Success)
        {
            s = bold.Groups["b1"].Success ? bold.Groups["b1"].Value : bold.Groups["b2"].Value;
        }

        // 3) Strip leftover markdown markers + hard-break backslashes; collapse whitespace/newlines.
        s = MarkdownNoise.Replace(s, " ");
        s = WhitespaceRun.Replace(s, " ").Trim();
        if (s.Length == 0)
        {
            return null;
        }

        // 4) After cleaning, a name that is still a URL, a search/category page title, or an over-long
        //    multi-field blob (no real product name runs 120+ chars) is noise, not a product → drop.
        if (LooksLikeUrl(s) || LooksLikePageTitle(s) || s.Length > MaxNameLength)
        {
            return null;
        }

        return s;
    }

    /// <summary>
    /// True when a "product name" is really a PAGE TITLE — a search/category/SEO page masquerading as an
    /// item (e.g. "Espresso Machine in Jordan | Find the Lowest Prices"). Pipe separators are the SEO
    /// tell, "lowest/best prices" phrasing (en/ar) the query-echo tell. A pipe alone is NOT a title:
    /// real product pages append "| StoreName" and carry a digit (model/capacity) before the pipe.
    /// </summary>
    public static bool LooksLikePageTitle(string text)
    {
        var trimmed = text.Trim();

        if (trimmed.Contains("lowest price", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("best price", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("أقل الأسعار", StringComparison.Ordinal)
            || trimmed.Contains("افضل الاسعار", StringComparison.Ordinal)
            || trimmed.Contains("أفضل الأسعار", StringComparison.Ordinal))
        {
            return true;
        }

        var pipe = trimmed.IndexOf('|');
        if (pipe > 0)
        {
            return !trimmed[..pipe].Any(char.IsDigit);
        }

        return false;
    }

    /// <summary>
    /// True when a url is a search/category LISTING page, not a product page — fine as a crawl entry
    /// point, never as a product card's identity. Query-parameter search urls and the common
    /// category-path conventions are the signals.
    /// </summary>
    public static bool IsListingPageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return false;
        }

        var query = u.Query;
        if (query.Contains("search=", StringComparison.OrdinalIgnoreCase) ||
            query.Contains("q=", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var path = u.AbsolutePath;
        // "/search/<digits>" is a PRODUCT on classified marketplaces (opensooq ad urls) — only a bare
        // /search or /search?<query> is a results page.
        var searchIsListing = path.Contains("/search", StringComparison.OrdinalIgnoreCase) &&
            !Regex.IsMatch(path, @"/search/\d+", RegexOptions.IgnoreCase);
        return path.Contains("/product-category/", StringComparison.OrdinalIgnoreCase)
            || searchIsListing
            || path.EndsWith("/products", StringComparison.OrdinalIgnoreCase)
            || (path.Contains("/collections/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("/products/", StringComparison.OrdinalIgnoreCase));
    }

    // Cheap brand guess: the first token of a shopping title is usually the brand.
    private static string? GuessBrand(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var first = title.Trim().Split(' ', '-', '|')[0];
        return first.Length is >= 2 and <= 20 ? first : null;
    }
}
