namespace Daleel.Web.Translation;

/// <summary>
/// Configuration for the DeepL-backed real-time translation layer. Resolved once at startup from the
/// environment (<c>DEEPL_API_KEY</c>) and registered as a singleton. Translation is a progressive
/// enhancement: when no key is configured the whole feature no-ops and the UI simply shows the original
/// text, so the app runs unchanged without DeepL.
/// </summary>
public sealed class TranslationOptions
{
    /// <summary>Master switch. Even when true, translation only runs if an API key is present.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The DeepL auth key. A free-tier key ends in <c>:fx</c> and routes to the free API host.</summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Translations are effectively permanent (a given source text always renders the same), so the cache
    /// never expires by default — but a configurable max age lets an operator force periodic re-fetches
    /// (e.g. after improving DeepL output). A cache row older than this is treated as a miss.
    /// </summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(3650);

    /// <summary>DeepL accepts up to 50 <c>text</c> entries per request; batch misses up to this cap.</summary>
    public int MaxBatchSize { get; init; } = 50;

    /// <summary>Injectable clock — overridden in tests for deterministic freshness checks.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// True when a usable key is configured. The deploy secret scaffolding seeds unset secrets with the
    /// literal <c>CHANGE_ME</c> placeholder, so treat that (and blank) as "not configured".
    /// </summary>
    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey) && !string.Equals(ApiKey, "CHANGE_ME", StringComparison.Ordinal);

    /// <summary>Builds options from the process environment (or an injected reader, for tests).</summary>
    public static TranslationOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var key = read("DEEPL_API_KEY");
        return new TranslationOptions { ApiKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim() };
    }
}
