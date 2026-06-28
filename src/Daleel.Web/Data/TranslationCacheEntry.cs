using System.ComponentModel.DataAnnotations;

namespace Daleel.Web.Data;

/// <summary>
/// One cached translation: a source text (identified by its SHA-256 hash) rendered into a target
/// language. Keyed by <c>(SourceHash, TargetLang)</c> so the same source can be cached independently per
/// language. Translations are permanent — a given source always produces the same output — so rows are
/// only re-fetched when older than <see cref="Translation.TranslationOptions.MaxAge"/>. Backs
/// <see cref="Translation.TranslationService"/>, which checks this table before ever calling DeepL.
/// </summary>
public sealed class TranslationCacheEntry
{
    public long Id { get; set; }

    /// <summary>SHA-256 hex of the source text. Half of the composite cache key; indexed with TargetLang.</summary>
    [Required]
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>The BCP-47 two-letter target language ("en" or "ar").</summary>
    [Required]
    public string TargetLang { get; set; } = string.Empty;

    /// <summary>The DeepL translation of the source text into <see cref="TargetLang"/>.</summary>
    [Required]
    public string TranslatedText { get; set; } = string.Empty;

    /// <summary>When this translation was cached. Drives the configurable freshness sweep.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
