using System.Security.Cryptography;
using System.Text;

namespace Daleel.Web.Api;

/// <summary>The full key + the only two artefacts of it that are ever persisted.</summary>
/// <param name="FullKey">The complete secret ("dlk_live_…"). Shown once at issue time, never stored.</param>
/// <param name="Hash">SHA-256 hex of <paramref name="FullKey"/> — the stored auth lookup key.</param>
/// <param name="Prefix">Display fragment ("dlk_live_AbCd1234…") so keys can be told apart.</param>
public sealed record GeneratedApiKey(string FullKey, string Hash, string Prefix);

/// <summary>
/// Generates and hashes B2B API keys. Format: <c>dlk_live_&lt;43 url-safe chars&gt;</c> — 32
/// cryptographically-random bytes, base64url-encoded without padding (43 chars). Only the SHA-256
/// hex of the full key is stored; verification is hash-and-compare, so a database leak never leaks
/// a usable key.
/// </summary>
public static class ApiKeyGenerator
{
    public const string LivePrefix = "dlk_live_";

    /// <summary>Chars of the key kept as the display prefix: "dlk_live_" + the first 8 random chars.
    /// (Enough to tell keys apart, far too little to reconstruct one.)</summary>
    private const int DisplayPrefixLength = 17;

    public static GeneratedApiKey Generate()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var body = Base64UrlEncode(secret); // 43 url-safe chars, no padding
        var fullKey = LivePrefix + body;
        return new GeneratedApiKey(fullKey, Hash(fullKey), fullKey[..DisplayPrefixLength]);
    }

    /// <summary>Lower-case SHA-256 hex of the full key string — what <c>ApiKey.Hash</c> stores.</summary>
    public static string Hash(string fullKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullKey))).ToLowerInvariant();

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
