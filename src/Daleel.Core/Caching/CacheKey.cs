using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Daleel.Core.Arabic;

namespace Daleel.Core.Caching;

/// <summary>
/// Builds stable, layer-prefixed cache keys. A key is <c>{layer}:{sha256}</c> where the hash is
/// computed over a canonical, normalized join of the inputs — so cosmetic differences ("iPhone 15"
/// vs " iphone  15 ", or Arabic orthographic variants) collapse to the same key and share a cache
/// entry. The <c>{layer}:</c> prefix lets the store classify and purge each layer independently.
/// </summary>
public static class CacheKey
{
    /// <summary>Layer for one provider's raw response to an exact provider+query+geo.</summary>
    public const string ProviderLayer = "provider";

    /// <summary>Layer for a whole normalized query+geo report (the full search result).</summary>
    public const string ResultLayer = "result";

    /// <summary>Key for the provider layer: identical provider+kind+query+geo ⇒ identical key.</summary>
    public static string ForProvider(
        string provider, string kind, string query,
        string? countryCode, string? languageCode, string? location, int maxResults)
    {
        var canonical = string.Join('|',
            Fold(provider),
            Fold(kind),
            Normalize(query),
            Fold(countryCode),
            Fold(languageCode),
            Normalize(location),
            maxResults.ToString(CultureInfo.InvariantCulture));
        return ProviderLayer + ":" + Hash(canonical);
    }

    /// <summary>Key for the result layer: identical normalized query+geo+language ⇒ identical key.</summary>
    public static string ForResult(string query, string? geo, string? language)
    {
        var canonical = string.Join('|', Normalize(query), Fold(geo), Fold(language));
        return ResultLayer + ":" + Hash(canonical);
    }

    /// <summary>The layer prefix of a key (the part before the first ':'), or the whole key if none.</summary>
    public static string LayerOf(string key)
    {
        var i = key.IndexOf(':');
        return i < 0 ? key : key[..i];
    }

    /// <summary>
    /// Canonicalizes free text for keying: Arabic/Unicode normalization (NFC, diacritic strip,
    /// letter folding, whitespace collapse — see <see cref="ArabicNormalizer"/>) plus case folding,
    /// so English and Arabic queries both key consistently.
    /// </summary>
    public static string Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : ArabicNormalizer.Normalize(text).ToLowerInvariant();

    /// <summary>Lighter canonicalization for short codes (provider, country, language): trim + lower.</summary>
    private static string Fold(string? token) => (token ?? "").Trim().ToLowerInvariant();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
