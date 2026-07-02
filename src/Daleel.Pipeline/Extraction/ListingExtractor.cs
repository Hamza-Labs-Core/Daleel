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
                        condition = new { type = "string" }
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

            // Extractors occasionally drop the source URL/bare domain into the name/title field instead of
            // the product name ("amazon.com/dp/…", "www.foo.io"). Reject a URL-shaped name in favour of the
            // model number — mirroring the LLM path's PickName — so a raw link never surfaces as a product.
            var name = rawName is not null && !LooksLikeUrl(rawName) ? rawName
                     : model is not null && !LooksLikeUrl(model) ? model
                     : null;

            // A product row with no usable name (missing or only a URL) and no price and no link is noise.
            if (string.IsNullOrWhiteSpace(name) && price is null && string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            // A row we could only identify by a URL is not a product — skip it rather than render a
            // nameless/link card (a common source of "links showing as product names" in the grid).
            if (string.IsNullOrWhiteSpace(name))
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
                Seller = Str(item, "seller")
            });
        }

        return listings;
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
