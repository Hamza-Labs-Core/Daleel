using System.Security.Cryptography;
using System.Text;
using Daleel.Web.Data;
using Daleel.Web.Services;

namespace Daleel.Web.Translation;

/// <summary>
/// Real-time, cache-first translation of dynamic content (brand/store/product descriptions, article
/// prose, review text, spec labels). The flow per text is: skip if it is already in the target language →
/// hash it → look it up in the Postgres cache → translate only the misses via DeepL (batched) → persist the
/// new translations. The whole service is best-effort: any failure falls back to the original text, so a
/// DeepL outage degrades the UI to bilingual-as-is rather than breaking a page.
/// </summary>
public interface ITranslationService
{
    /// <summary>True when translation is switched on AND a DeepL key is configured.</summary>
    bool Enabled { get; }

    /// <summary>Translates one text into <paramref name="targetLang"/>, returning the original on any miss/failure.</summary>
    Task<string> TranslateAsync(string text, string targetLang, CancellationToken ct = default);

    /// <summary>Translates a batch in one cache lookup + (where needed) batched DeepL calls; result aligns 1:1.</summary>
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default);
}

public sealed class TranslationService : ITranslationService
{
    private readonly ITranslator _translator;
    private readonly ITranslationRepository _repo;
    private readonly TranslationOptions _options;
    private readonly ILogger<TranslationService> _logger;

    public TranslationService(
        ITranslator translator, ITranslationRepository repo, TranslationOptions options,
        ILogger<TranslationService> logger)
    {
        _translator = translator;
        _repo = repo;
        _options = options;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled && _translator.IsConfigured;

    /// <summary>
    /// Heuristic for whether a text needs translating into <paramref name="targetLang"/>. Cheap and
    /// script-based (no API call): an Arabic-script string is "Arabic", anything else is treated as
    /// English/Latin. So we translate to Arabic only non-Arabic text, and to English only Arabic text —
    /// content already in the target language is left untouched.
    /// </summary>
    public static bool NeedsTranslation(string? text, string targetLang)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var isArabic = Catalog.IsArabic(text);
        return targetLang.ToLowerInvariant() switch
        {
            "ar" => !isArabic,
            "en" => isArabic,
            _ => false
        };
    }

    public async Task<string> TranslateAsync(string text, string targetLang, CancellationToken ct = default)
    {
        var result = await TranslateAsync(new[] { text }, targetLang, ct);
        return result.Count > 0 ? result[0] : text;
    }

    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
    {
        if (texts.Count == 0 || !Enabled)
        {
            return texts;
        }

        // Start from the originals; only the entries that genuinely need translating get replaced.
        var output = texts.ToArray();

        // Map each distinct source text that needs translating to its hash, and remember which output slots
        // use it (so identical strings are translated/cached once and fanned back out).
        var hashByText = new Dictionary<string, string>(StringComparer.Ordinal);
        var slotsByHash = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < texts.Count; i++)
        {
            var text = texts[i];
            if (!NeedsTranslation(text, targetLang))
            {
                continue;
            }

            if (!hashByText.TryGetValue(text, out var hash))
            {
                hash = Hash(text);
                hashByText[text] = hash;
            }
            (slotsByHash.TryGetValue(hash, out var slots) ? slots : slotsByHash[hash] = new List<int>()).Add(i);
        }

        if (slotsByHash.Count == 0)
        {
            return output; // everything already in the target language
        }

        var hashToText = hashByText.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

        try
        {
            // 1. Cache: one batched composite-key lookup for all needed hashes.
            var notOlderThan = _options.Now() - _options.MaxAge;
            var cached = await _repo.GetFreshAsync(slotsByHash.Keys.ToList(), targetLang, notOlderThan, ct);
            foreach (var (hash, translated) in cached)
            {
                Fill(output, slotsByHash[hash], translated);
            }

            // 2. Misses: translate via DeepL in batches, then persist.
            var missHashes = slotsByHash.Keys.Where(h => !cached.ContainsKey(h)).ToList();
            if (missHashes.Count > 0)
            {
                await TranslateMissesAsync(missHashes, hashToText, slotsByHash, targetLang, output, ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort: a cache or DeepL failure must never break the page. Keep whatever we resolved
            // (cache hits already applied) and leave the rest as the original text.
            _logger.LogWarning(ex, "Translation to {Lang} failed for {Count} text(s); falling back to originals.",
                targetLang, slotsByHash.Count);
        }

        return output;
    }

    private async Task TranslateMissesAsync(
        List<string> missHashes, IReadOnlyDictionary<string, string> hashToText,
        IReadOnlyDictionary<string, List<int>> slotsByHash, string targetLang, string[] output,
        CancellationToken ct)
    {
        var fresh = new List<TranslationCacheEntry>(missHashes.Count);
        var now = _options.Now();

        for (var start = 0; start < missHashes.Count; start += _options.MaxBatchSize)
        {
            var batchHashes = missHashes.GetRange(start, Math.Min(_options.MaxBatchSize, missHashes.Count - start));
            var batchTexts = batchHashes.Select(h => hashToText[h]).ToList();

            var translated = await _translator.TranslateAsync(batchTexts, targetLang, ct);
            for (var i = 0; i < batchHashes.Count; i++)
            {
                var hash = batchHashes[i];
                var value = translated[i];
                Fill(output, slotsByHash[hash], value);
                fresh.Add(new TranslationCacheEntry
                {
                    SourceHash = hash, TargetLang = targetLang, TranslatedText = value, CreatedAt = now
                });
            }
        }

        await _repo.SaveAsync(fresh, ct);
    }

    private static void Fill(string[] output, List<int> slots, string value)
    {
        foreach (var slot in slots)
        {
            output[slot] = value;
        }
    }

    /// <summary>SHA-256 hex of the source text — the stable cache key half.</summary>
    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
