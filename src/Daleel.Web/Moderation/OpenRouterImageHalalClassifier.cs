using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Daleel.Core.Caching;
using Daleel.Core.Llm;
using Daleel.Core.Moderation;
using Daleel.Search.Http;
using Daleel.Web.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Daleel.Web.Moderation;

/// <summary>
/// <see cref="IHalalImageClassifier"/> backed by OpenRouter's OpenAI-compatible chat-completions
/// endpoint, sending a batch of <c>image_url</c> parts to a vision-capable model. Mirrors the wire
/// format of <see cref="Identification.VisionMatcher"/> (the text-only <c>ILlmClient</c> can't
/// speak multimodal). Verdicts are cached by image URL via <see cref="ICacheStore"/> so repeated
/// searches don't re-pay to screen the same product photos.
/// </summary>
/// <remarks>
/// Failure contract: any transport/parse error returns the verdicts gathered so far (possibly
/// empty) — image moderation is best-effort and must never fault a search.
/// </remarks>
public sealed class OpenRouterImageHalalClassifier : IHalalImageClassifier, IDisposable
{
    /// <summary>Default vision model — same default as the product-identification matcher.</summary>
    public const string DefaultModel = "anthropic/claude-sonnet-5";

    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string Referer = "https://github.com/Hamza-Labs-Core/Daleel";
    private const string Title = "Daleel";

    /// <summary>Images per completion call. Keeps the request small and verdict ids reliable.</summary>
    private const int BatchSize = 8;

    /// <summary>Hard per-call timeout; a hung model degrades to "no verdicts", never a fault.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(90);

    /// <summary>How long a per-image verdict stays cached.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    private const string CacheKeyPrefix = "halal-image:";

    /// <summary>The built-in vision policy prompt — composed from <see cref="VisionPolicy.DefaultRules"/>,
    /// the fallback used when no admin rule list is configured (or a rule read fails).</summary>
    public static readonly string DefaultSystemPrompt = VisionPolicy.Compose(VisionPolicy.DefaultRules);

    private readonly string _apiKey;
    private readonly string _model;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ICacheStore? _cache;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ILogger<OpenRouterImageHalalClassifier> _logger;

    public bool IsConfigured => true;

    public OpenRouterImageHalalClassifier(
        string apiKey, string? model, ILogger<OpenRouterImageHalalClassifier> logger,
        ICacheStore? cache = null, HttpClient? http = null, IServiceScopeFactory? scopeFactory = null)
    {
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        _logger = logger;
        _cache = cache;
        _scopeFactory = scopeFactory;
        _ownsHttp = http is null;
        _http = http ?? SharedHttpHandler.CreateClient();
        if (_http.Timeout == default || _http.Timeout == TimeSpan.FromSeconds(100))
        {
            _http.Timeout = TimeSpan.FromMinutes(3);
        }
    }

    public async Task<ImageClassifierResult> ClassifyAsync(
        IReadOnlyList<string> imageUrls, CancellationToken ct = default, bool bypassCache = false)
    {
        var flaggedVerdicts = new List<ImageVerdict>();
        var unscreened = new List<string>();
        var toClassify = new List<string>();

        foreach (var url in imageUrls.Where(IsHttpUrl).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Re-evaluation passes bypassCache: ignore the stored verdict so a rule change actually
            // re-judges the image (the fresh verdict is written back, refreshing the cache).
            if (!bypassCache && await ReadCachedAsync(url, ct).ConfigureAwait(false) is { } cached)
            {
                // A cached verdict is a completed screen: haram → flagged; clean → nothing to record.
                if (cached.IsHaram)
                {
                    flaggedVerdicts.Add(cached);
                }
            }
            else
            {
                toClassify.Add(url);
            }
        }

        // Resolve the effective policy once per screen from the admin rule LIST: the prompt composed from
        // the active rules, plus the categories those rules may flag with. Read once (not per batch) so
        // all batches judge by the same policy.
        var (prompt, allowed) = await ResolvePolicyAsync(ct).ConfigureAwait(false);

        for (var offset = 0; offset < toClassify.Count; offset += BatchSize)
        {
            var batch = toClassify.Skip(offset).Take(BatchSize).ToList();
            var flagged = await ClassifyBatchAsync(batch, prompt, allowed, ct).ConfigureAwait(false);
            if (flagged is null)
            {
                // The screen could NOT run for this batch (HTTP 402 out-of-credits / 429 / 5xx /
                // timeout / unparseable). Report the URLs as UNSCREENED so the caller fails CLOSED
                // (hide + hold) — never mistaken for "clean". Do NOT cache: a retry must re-attempt.
                unscreened.AddRange(batch);
                continue;
            }

            foreach (var url in batch)
            {
                var verdict = flagged.GetValueOrDefault(url)
                    ?? new ImageVerdict(url, false, null, 0.0, null);
                await WriteCachedAsync(verdict, ct).ConfigureAwait(false);
                if (verdict.IsHaram)
                {
                    flaggedVerdicts.Add(verdict);
                }
            }
        }

        return new ImageClassifierResult(flaggedVerdicts, unscreened);
    }

    /// <summary>
    /// The effective vision policy for this screen: the prompt composed from the admin rule LIST and the
    /// category set those rules may flag with. Best-effort — no scope factory (e.g. tests), no active
    /// rules, or any read failure falls back to the built-in default policy so image moderation is never
    /// blocked by a config lookup.
    /// </summary>
    private async Task<(string Prompt, IReadOnlySet<string> Allowed)> ResolvePolicyAsync(CancellationToken ct)
    {
        var fallback = (DefaultSystemPrompt, VisionPolicy.AllowedCategories(VisionPolicy.DefaultRules));
        if (_scopeFactory is null)
        {
            return fallback;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var rules = await scope.ServiceProvider.GetRequiredService<IImageModerationRuleRepository>()
                .ActiveRulesAsync(ct).ConfigureAwait(false);
            if (rules.Count == 0)
            {
                return fallback;
            }

            return (VisionPolicy.Compose(rules), VisionPolicy.AllowedCategories(rules));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Image moderation rule read failed; using the built-in default policy");
            return fallback;
        }
    }

    /// <summary>Runs one vision call over a batch. Returns null when the call failed entirely.</summary>
    private async Task<Dictionary<string, ImageVerdict>?> ClassifyBatchAsync(
        IReadOnlyList<string> batch, string systemPrompt, IReadOnlySet<string> allowedCategories, CancellationToken ct)
    {
        var content = new List<object>
        {
            new { type = "text", text = $"Classify these {batch.Count} images (numbered 0-{batch.Count - 1} in order)." }
        };
        content.AddRange(batch.Select(url => (object)new { type = "image_url", image_url = new { url } }));

        var payload = new
        {
            model = _model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content }
            }
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CallTimeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Headers.Add("HTTP-Referer", Referer);
            req.Headers.Add("X-Title", Title);

            using var resp = await _http.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Image moderation HTTP {Status} for a batch of {Count}", (int)resp.StatusCode, batch.Count);
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            return Parse(body, batch, allowedCategories);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine outer cancellation (cost cap / user / deadline) propagates
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Image moderation failed for a batch of {Count}", batch.Count);
            return null;
        }
    }

    /// <summary>Extracts flagged-image verdicts from an OpenAI-shaped chat-completions response. The
    /// <paramref name="allowedCategories"/> are the categories the active rules may flag with (defaults to
    /// the built-in halal set); a verdict tagged outside that set — or with a never-filtered category — is
    /// dropped so the model can't invent junk categories.</summary>
    internal static Dictionary<string, ImageVerdict>? Parse(
        string responseBody, IReadOnlyList<string> batch, IReadOnlySet<string>? allowedCategories = null)
    {
        var allowed = allowedCategories ?? HalalPolicy.AllowedCategories;
        string? content;
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                return null;
            }

            content = ExtractContent(choices[0].GetProperty("message"));
        }
        catch (JsonException)
        {
            return null;
        }

        var dtos = LlmJson.Deserialize<List<FlagDto>>(content);
        if (dtos is null)
        {
            return null;
        }

        var flagged = new Dictionary<string, ImageVerdict>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in dtos)
        {
            var category = dto.Category?.Trim().ToLowerInvariant();
            if (dto.Index < 0 || dto.Index >= batch.Count
                || category is null
                || !allowed.Contains(category)
                || HalalPolicy.NeverFiltered.Contains(category))
            {
                continue;
            }

            var url = batch[dto.Index];
            flagged[url] = new ImageVerdict(
                url, true, category, Math.Clamp(dto.Confidence, 0.0, 1.0),
                string.IsNullOrWhiteSpace(dto.Reason) ? null : dto.Reason.Trim());
        }

        return flagged;
    }

    /// <summary>The message content is usually a string, but some providers return an array of parts.</summary>
    private static string? ExtractContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    sb.Append(t.GetString());
                }
            }

            return sb.ToString();
        }

        return null;
    }

    private async Task<ImageVerdict?> ReadCachedAsync(string url, CancellationToken ct)
    {
        if (_cache is null)
        {
            return null;
        }

        try
        {
            var json = await _cache.GetAsync(CacheKeyPrefix + ModerationKeys.HashContent(url), ct).ConfigureAwait(false);
            return json is null ? null : JsonSerializer.Deserialize<CachedVerdict>(json)?.ToVerdict(url);
        }
        catch
        {
            return null; // cache trouble must never block moderation
        }
    }

    private async Task WriteCachedAsync(ImageVerdict verdict, CancellationToken ct)
    {
        if (_cache is null)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(CachedVerdict.From(verdict));
            await _cache.SetAsync(CacheKeyPrefix + ModerationKeys.HashContent(verdict.ImageUrl), json, CacheTtl, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
    }

    private static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private sealed class FlagDto
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("confidence")] public double Confidence { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
    }

    private sealed class CachedVerdict
    {
        public bool Haram { get; set; }
        public string? Category { get; set; }
        public double Confidence { get; set; }
        public string? Reason { get; set; }

        public static CachedVerdict From(ImageVerdict v) => new()
        {
            Haram = v.IsHaram, Category = v.Category, Confidence = v.Confidence, Reason = v.Reason
        };

        public ImageVerdict ToVerdict(string url) => new(url, Haram, Category, Confidence, Reason);
    }
}
