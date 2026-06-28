using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Daleel.Search.Http;

namespace Daleel.Web.Translation;

/// <summary>
/// The low-level outbound translator. Abstracted behind an interface so <see cref="TranslationService"/>
/// (and its tests) depend on translation, not on DeepL or HTTP specifically.
/// </summary>
public interface ITranslator
{
    /// <summary>True when a usable provider key is configured; false ⇒ the service short-circuits to originals.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Translates a batch of texts into <paramref name="targetLang"/> (a two-letter BCP-47 code), returning a
    /// list aligned 1:1 with the input. Throws on transport/API failure — the caller decides the fallback.
    /// </summary>
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default);
}

/// <summary>
/// DeepL implementation of <see cref="ITranslator"/>. Calls <c>POST /v2/translate</c> with the document's
/// text array and target language; DeepL auto-detects the source language. Free-tier keys (suffix
/// <c>:fx</c>) route to the free API host. Shares the app's pooled, SSRF-guarded <see cref="SharedHttpHandler"/>
/// like every other outbound provider rather than spinning up its own socket pool.
/// </summary>
public sealed class DeepLClient : ITranslator
{
    private const string ProHost = "https://api.deepl.com";
    private const string FreeHost = "https://api-free.deepl.com";

    private readonly TranslationOptions _options;
    private readonly HttpClient _http;

    public DeepLClient(TranslationOptions options, HttpClient? http = null)
    {
        _options = options;
        _http = http ?? SharedHttpHandler.CreateClient();
    }

    public bool IsConfigured => _options.HasKey;

    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
    {
        if (texts.Count == 0)
        {
            return Array.Empty<string>();
        }
        if (!IsConfigured)
        {
            return texts; // nothing configured — echo the inputs so callers degrade to originals
        }

        var host = _options.ApiKey!.EndsWith(":fx", StringComparison.Ordinal) ? FreeHost : ProHost;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{host}/v2/translate")
        {
            Content = JsonContent.Create(new DeepLRequest(texts, ToDeepLTarget(targetLang)))
        };
        // DeepL authenticates with a custom scheme: "Authorization: DeepL-Auth-Key <key>".
        request.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {_options.ApiKey}");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<DeepLResponse>(ct);
        var translations = payload?.Translations;
        if (translations is null || translations.Count != texts.Count)
        {
            // A misaligned response would silently corrupt the cache (wrong text under a hash). Refuse it.
            throw new InvalidOperationException(
                $"DeepL returned {translations?.Count ?? 0} translations for {texts.Count} inputs.");
        }

        return translations.Select(t => t.Text ?? string.Empty).ToList();
    }

    /// <summary>Maps a two-letter UI culture to DeepL's target-language code. English targets prefer a region.</summary>
    private static string ToDeepLTarget(string lang) => lang.ToLowerInvariant() switch
    {
        "ar" => "AR",
        "en" => "EN-US",
        "fr" => "FR",
        _ => lang.ToUpperInvariant()
    };

    private sealed record DeepLRequest(
        [property: JsonPropertyName("text")] IReadOnlyList<string> Text,
        [property: JsonPropertyName("target_lang")] string TargetLang);

    private sealed class DeepLResponse
    {
        [JsonPropertyName("translations")]
        public List<DeepLTranslation>? Translations { get; set; }
    }

    private sealed class DeepLTranslation
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
