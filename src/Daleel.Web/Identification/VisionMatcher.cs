using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Daleel.Core.Llm;
using Daleel.Core.Observability;
using Daleel.Search.Http;

namespace Daleel.Web.Identification;

/// <summary>The verdict of a single vision comparison between a store photo and a brand catalogue image.</summary>
public sealed record VisionMatchResult(bool SameProduct, double Confidence, string? ModelName)
{
    /// <summary>The neutral "couldn't tell / not the same" result, used on any failure or skip.</summary>
    public static VisionMatchResult NoMatch { get; } = new(false, 0.0, null);
}

/// <summary>
/// Compares two product images with a vision LLM to answer "are these the same product, and which model
/// is it?". This is the bridge that lets Daleel identify a Jordanian store listing whose model name is
/// vague or wrong: the store's photo is matched against the brand's official catalogue images.
/// </summary>
public interface IVisionMatcher
{
    /// <summary>True when a vision-capable LLM key is available and comparisons will actually run.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Asks the vision model whether <paramref name="storeImageUrl"/> and <paramref name="brandImageUrl"/>
    /// show the same product. <paramref name="candidateModelName"/> is an optional hint (the brand model's
    /// name). Returns <see cref="VisionMatchResult.NoMatch"/> on any failure — never throws.
    /// </summary>
    Task<VisionMatchResult> CompareAsync(
        string storeImageUrl, string brandImageUrl, string? candidateModelName, CancellationToken ct = default);
}

/// <summary>Inert matcher used when no LLM key is configured: every comparison is a non-match.</summary>
public sealed class NullVisionMatcher : IVisionMatcher
{
    public bool IsConfigured => false;

    public Task<VisionMatchResult> CompareAsync(
        string storeImageUrl, string brandImageUrl, string? candidateModelName, CancellationToken ct = default) =>
        Task.FromResult(VisionMatchResult.NoMatch);
}

/// <summary>
/// <see cref="IVisionMatcher"/> backed by OpenRouter's OpenAI-compatible chat-completions endpoint, sending
/// two <c>image_url</c> content parts to a vision-capable model. Mirrors the wire format of
/// <c>OpenRouterClient</c> but speaks multimodal, which the text-only <c>ILlmClient</c> abstraction can't.
/// </summary>
public sealed class VisionMatcher : IVisionMatcher, IDisposable
{
    /// <summary>Default vision model — Claude Sonnet 5 (high-resolution vision, current Sonnet tier).</summary>
    public const string DefaultModel = "anthropic/claude-sonnet-5";

    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string Referer = "https://github.com/Hamza-Labs-Core/Daleel";
    private const string Title = "Daleel";

    /// <summary>Hard per-call timeout for a single vision comparison; a hung model degrades to NoMatch.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    private const string SystemPrompt =
        "You are a product-identification expert comparing two product photographs. Decide whether both " +
        "images show the SAME physical product (same brand, same model line, same variant). Ignore " +
        "background, watermark, angle and lighting differences. Respond with ONLY a JSON object: " +
        "{\"same_product\": boolean, \"confidence\": number between 0 and 1, \"model_name\": string}. " +
        "Set model_name to the specific model you recognize, or null if unsure.";

    private readonly string _apiKey;
    private readonly string _model;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ILogger<VisionMatcher> _logger;

    public bool IsConfigured => true;

    public VisionMatcher(string apiKey, string? model, ILogger<VisionMatcher> logger, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        _logger = logger;
        _ownsHttp = http is null;
        _http = http ?? SharedHttpHandler.CreateClient();
        if (_http.Timeout == default || _http.Timeout == TimeSpan.FromSeconds(100))
        {
            _http.Timeout = TimeSpan.FromMinutes(2);
        }
    }

    public async Task<VisionMatchResult> CompareAsync(
        string storeImageUrl, string brandImageUrl, string? candidateModelName, CancellationToken ct = default)
    {
        if (!IsHttpUrl(storeImageUrl) || !IsHttpUrl(brandImageUrl))
        {
            return VisionMatchResult.NoMatch;
        }

        var userText = candidateModelName is { Length: > 0 } hint
            ? $"The second image is believed to be \"{hint}\". Is the first image the same product?"
            : "Is the first image the same product as the second image?";

        var messages = new object[]
        {
            new { role = "system", content = SystemPrompt },
            new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = userText },
                    new { type = "image_url", image_url = new { url = storeImageUrl } },
                    new { type = "image_url", image_url = new { url = brandImageUrl } }
                }
            }
        };
        // Group this vision call under the owning search's session id (OpenRouter `user` field), the
        // same as every text call — vision ID runs inside the drain's AmbientLlmSession scope.
        var payload = AmbientLlmSession.SessionId is { Length: > 0 } session
            ? (object)new { model = _model, messages, user = session }
            : new { model = _model, messages };

        // Bound the call deterministically: linked CTS that auto-cancels after CallTimeout but still
        // honours a genuine outer cancellation.
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
                _logger.LogDebug("Vision match HTTP {Status} comparing {Store} vs {Brand}",
                    (int)resp.StatusCode, storeImageUrl, brandImageUrl);
                return VisionMatchResult.NoMatch;
            }

            var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            return Parse(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine outer cancellation (cost cap / user / workflow deadline) propagates
        }
        catch (OperationCanceledException ex)
        {
            // Per-call timeout: degrade to NoMatch like any other vision failure.
            _logger.LogDebug(ex, "Vision match timed out after {Seconds}s comparing {Store} vs {Brand}",
                CallTimeout.TotalSeconds, storeImageUrl, brandImageUrl);
            return VisionMatchResult.NoMatch;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Vision match failed comparing {Store} vs {Brand}", storeImageUrl, brandImageUrl);
            return VisionMatchResult.NoMatch;
        }
    }

    /// <summary>Pulls the assistant's JSON verdict out of an OpenAI-shaped chat-completions response.</summary>
    internal static VisionMatchResult Parse(string responseBody)
    {
        string? content;
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                return VisionMatchResult.NoMatch;
            }

            var message = choices[0].GetProperty("message");
            content = ExtractContent(message);
        }
        catch (JsonException)
        {
            return VisionMatchResult.NoMatch;
        }

        var dto = LlmJson.Deserialize<VisionDto>(content);
        if (dto is null)
        {
            return VisionMatchResult.NoMatch;
        }

        var confidence = Math.Clamp(dto.Confidence, 0.0, 1.0);
        return new VisionMatchResult(dto.SameProduct, confidence,
            string.IsNullOrWhiteSpace(dto.ModelName) ? null : dto.ModelName!.Trim());
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

    private sealed class VisionDto
    {
        [JsonPropertyName("same_product")] public bool SameProduct { get; set; }
        [JsonPropertyName("confidence")] public double Confidence { get; set; }
        [JsonPropertyName("model_name")] public string? ModelName { get; set; }
    }
}
