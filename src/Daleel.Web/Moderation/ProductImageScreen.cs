using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Daleel.Core.Caching;
using Daleel.Core.Moderation;
using Daleel.Core.Observability;
using Daleel.Search.Http;

namespace Daleel.Web.Moderation;

/// <summary>
/// A vision screen that keeps only CLEAN PRODUCT SHOTS on cards. The extraction LLM often picks a store's
/// hero image — which may be a lifestyle/room scene, a promotional banner, or a logo, not the product —
/// and a filename blocklist can't tell those apart. This actually LOOKS at each image and reports the ones
/// that are not a clean product photo, so the image gate can hide them (the card then shows a real product
/// photo or a clean placeholder). URL-only and product-agnostic: it judges "is this a product shot" by the
/// image's own composition, so it needs no per-item context. Verdicts are cached per image URL.
/// </summary>
public interface IProductImageScreen
{
    bool IsConfigured { get; }

    /// <summary>
    /// The subset of <paramref name="imageUrls"/> that are NOT clean product shots (to hide). Best-effort
    /// and FAIL-OPEN: any transport/parse failure yields an empty set (reject nothing), so a vision outage
    /// never blanks images — it only ever REMOVES a bad photo, never hides a good one it couldn't judge.
    /// </summary>
    Task<IReadOnlySet<string>> RejectNonProductShotsAsync(IReadOnlyList<string> imageUrls, CancellationToken ct = default);
}

/// <summary>No key configured ⇒ screen is inert (rejects nothing).</summary>
public sealed class NullProductImageScreen : IProductImageScreen
{
    public bool IsConfigured => false;
    public Task<IReadOnlySet<string>> RejectNonProductShotsAsync(IReadOnlyList<string> imageUrls, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
}

/// <summary>OpenRouter multimodal implementation of <see cref="IProductImageScreen"/>.</summary>
public sealed class OpenRouterProductImageScreen : IProductImageScreen, IDisposable
{
    public const string DefaultModel = "anthropic/claude-sonnet-5";

    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string Referer = "https://github.com/Hamza-Labs-Core/Daleel";
    private const string Title = "Daleel";
    private const int BatchSize = 8;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);
    // Bump the version when the screening CRITERIA change so stale verdicts don't pin an image to an old
    // decision (v1 rejected in-room/lifestyle product shots; v2 keeps them and rejects only logos/graphics).
    private const string CacheKeyPrefix = "product-shot:v2:";

    internal const string SystemPrompt =
        "You screen e-commerce images. For EACH numbered image, decide whether it actually DEPICTS THE " +
        "PRODUCT being sold. KEEP (do NOT reject) any photo in which the product itself is clearly visible — " +
        "whether it is on a plain/white studio background OR styled in a room, in use, or in a marketing/" +
        "lifestyle setting; a real photo of the product is fine regardless of styling. REJECT ONLY images " +
        "that do NOT show the product: a logo or brand/marketplace card, a promotional banner or advert that " +
        "is mostly text/graphics, a coupon, a pure text or graphic image, a generic 'no image' placeholder, " +
        "or a collage of many different products. Reply with ONLY this JSON object, no prose: " +
        "{\"reject\":[<indices (0-based) of images that do NOT show the product>]}. When you are unsure " +
        "whether the product is visible, do NOT reject (leave the image out of the list).";

    private readonly string _apiKey;
    private readonly IVisionModelResolver _modelResolver;
    private readonly ILogger<OpenRouterProductImageScreen> _logger;
    private readonly ICacheStore? _cache;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public OpenRouterProductImageScreen(
        string apiKey, IVisionModelResolver? modelResolver, ILogger<OpenRouterProductImageScreen> logger,
        ICacheStore? cache = null, HttpClient? http = null)
    {
        _apiKey = apiKey;
        // Resolved per call, never captured: this is a singleton, so a model read here would pin the
        // screen to whatever was configured at startup (see VisionModelResolver).
        _modelResolver = modelResolver ?? new VisionModelResolver();
        _logger = logger;
        _cache = cache;
        _ownsHttp = http is null;
        _http = http ?? SharedHttpHandler.CreateClient();
        if (_http.Timeout == default || _http.Timeout == TimeSpan.FromSeconds(100))
        {
            _http.Timeout = TimeSpan.FromMinutes(3);
        }
    }

    public bool IsConfigured => true;

    public async Task<IReadOnlySet<string>> RejectNonProductShotsAsync(
        IReadOnlyList<string> imageUrls, CancellationToken ct = default)
    {
        var reject = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toScreen = new List<string>();

        foreach (var url in imageUrls.Where(IsHttpUrl).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await ReadCachedAsync(url, ct).ConfigureAwait(false) is { } cached)
            {
                if (cached)
                {
                    reject.Add(url);
                }
            }
            else
            {
                toScreen.Add(url);
            }
        }

        for (var offset = 0; offset < toScreen.Count; offset += BatchSize)
        {
            var batch = toScreen.Skip(offset).Take(BatchSize).ToList();
            var rejected = await ScreenBatchAsync(batch, ct).ConfigureAwait(false);
            if (rejected is null)
            {
                continue; // FAIL-OPEN: the call failed — reject nothing, don't cache, a retry re-screens.
            }

            for (var i = 0; i < batch.Count; i++)
            {
                var isReject = rejected.Contains(i);
                await WriteCachedAsync(batch[i], isReject, ct).ConfigureAwait(false);
                if (isReject)
                {
                    reject.Add(batch[i]);
                }
            }
        }

        return reject;
    }

    /// <summary>One vision call over a batch. Returns the 0-based indices to reject, or null when the call
    /// failed entirely (so the caller fails open).</summary>
    private async Task<HashSet<int>?> ScreenBatchAsync(IReadOnlyList<string> batch, CancellationToken ct)
    {
        var content = new List<object>
        {
            new { type = "text", text = $"Judge these {batch.Count} images (numbered 0-{batch.Count - 1} in order)." }
        };
        content.AddRange(batch.Select(url => (object)new { type = "image_url", image_url = new { url } }));

        var messages = new object[]
        {
            new { role = "system", content = SystemPrompt },
            new { role = "user", content }
        };
        var model = await _modelResolver.ResolveAsync(DefaultModel, ct).ConfigureAwait(false);
        // Same OpenRouter session_id as every other call this search makes (sticky routing + observability).
        var payload = AmbientLlmSession.SessionId is { Length: > 0 } session
            ? (object)new { model, messages, session_id = session }
            : new { model, messages };

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
                _logger.LogDebug("Product-image screen HTTP {Status} for a batch of {Count}", (int)resp.StatusCode, batch.Count);
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            return Parse(body, batch.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine outer cancellation propagates
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Product-image screen failed for a batch of {Count}", batch.Count);
            return null;
        }
    }

    /// <summary>Parses the reject indices from an OpenAI-shaped chat response. Null on any parse failure
    /// (fail-open). Out-of-range indices are ignored.</summary>
    internal static HashSet<int>? Parse(string responseBody, int batchCount)
    {
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

        if (content is null)
        {
            return null;
        }

        var dto = Daleel.Core.Llm.LlmJson.Deserialize<RejectDto>(content);
        if (dto?.Reject is null)
        {
            return null;
        }

        return dto.Reject.Where(i => i >= 0 && i < batchCount).ToHashSet();
    }

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

    private async Task<bool?> ReadCachedAsync(string url, CancellationToken ct)
    {
        if (_cache is null)
        {
            return null;
        }

        try
        {
            var v = await _cache.GetAsync(CacheKeyPrefix + ModerationKeys.HashContent(url), ct).ConfigureAwait(false);
            return v switch { "1" => true, "0" => false, _ => (bool?)null };
        }
        catch
        {
            return null; // cache trouble must never block the screen
        }
    }

    private async Task WriteCachedAsync(string url, bool reject, CancellationToken ct)
    {
        if (_cache is null)
        {
            return;
        }

        try
        {
            await _cache.SetAsync(CacheKeyPrefix + ModerationKeys.HashContent(url), reject ? "1" : "0", CacheTtl, ct)
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

    private sealed class RejectDto
    {
        [JsonPropertyName("reject")] public List<int>? Reject { get; set; }
    }
}
