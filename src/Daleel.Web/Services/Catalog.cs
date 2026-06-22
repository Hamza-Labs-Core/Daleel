using Daleel.Core.Geo;

namespace Daleel.Web.Services;

/// <summary>A market option presented in the UI, pairing a <see cref="GeoProfile"/> with a localized label.</summary>
public sealed record GeoOption(string Key, string English, string Arabic, string Flag)
{
    /// <summary>The underlying market profile (languages, currency, center city, …).</summary>
    public GeoProfile Profile => GeoProfiles.ResolveOrDefault(Key);

    /// <summary>"🇯🇴 Jordan · الأردن" — a single bilingual label for dropdowns.</summary>
    public string Display => $"{Flag} {English} · {Arabic}";
}

/// <summary>An OpenRouter model offered in the model selector.</summary>
public sealed record ModelOption(string Id, string Label, string Note);

/// <summary>
/// Static UI catalogs (markets, models) plus small bilingual helpers. Kept separate from the
/// domain so the web layer can present Arabic labels without polluting <see cref="GeoProfiles"/>.
/// </summary>
public static class Catalog
{
    /// <summary>Markets the UI exposes, ordered the way the brief lists them.</summary>
    public static readonly IReadOnlyList<GeoOption> Geos = new[]
    {
        new GeoOption("jordan", "Jordan", "الأردن", "🇯🇴"),
        new GeoOption("usa", "USA", "الولايات المتحدة", "🇺🇸"),
        new GeoOption("saudi", "Saudi Arabia", "السعودية", "🇸🇦"),
        new GeoOption("uae", "UAE", "الإمارات", "🇦🇪"),
        new GeoOption("egypt", "Egypt", "مصر", "🇪🇬"),
    };

    /// <summary>
    /// Curated OpenRouter model ids. OpenRouter takes a single key and routes to every provider,
    /// so these are <c>vendor/model</c> slugs. The first entry matches the client default.
    /// </summary>
    public static readonly IReadOnlyList<ModelOption> Models = new[]
    {
        new ModelOption("anthropic/claude-sonnet-4", "Claude Sonnet 4", "Balanced · strong Arabic"),
        new ModelOption("anthropic/claude-opus-4.1", "Claude Opus 4.1", "Highest quality"),
        new ModelOption("openai/gpt-4o", "GPT-4o", "Fast, capable"),
        new ModelOption("openai/gpt-4o-mini", "GPT-4o mini", "Cheapest OpenAI"),
        new ModelOption("google/gemini-2.5-flash", "Gemini 2.5 Flash", "Fast & cheap"),
        new ModelOption("google/gemini-2.5-pro", "Gemini 2.5 Pro", "Long context"),
        new ModelOption("meta-llama/llama-3.3-70b-instruct", "Llama 3.3 70B", "Open weights"),
        new ModelOption("deepseek/deepseek-chat", "DeepSeek Chat", "Value pick"),
    };

    /// <summary>The default model id (mirrors <c>OpenRouterClient.DefaultModel</c>).</summary>
    public const string DefaultModel = "anthropic/claude-sonnet-4";

    /// <summary>Resolves a geo option by key, falling back to the first entry (Jordan).</summary>
    public static GeoOption ResolveGeo(string? key) =>
        Geos.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Geos[0];

    /// <summary>
    /// True when the text contains any Arabic-script character. Drives <c>dir="rtl"</c> and
    /// font/alignment choices for rendered results.
    /// </summary>
    public static bool IsArabic(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var c in text)
        {
            // Arabic (0600–06FF), Arabic Supplement (0750–077F), Presentation Forms-A/B.
            if (c is >= '؀' and <= 'ۿ'
                or >= 'ݐ' and <= 'ݿ'
                or >= 'ﭐ' and <= '﷿'
                or >= 'ﹰ' and <= '﻿')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Picks <c>"rtl"</c> or <c>"ltr"</c> for a block of text.</summary>
    public static string Dir(string? text) => IsArabic(text) ? "rtl" : "ltr";
}
