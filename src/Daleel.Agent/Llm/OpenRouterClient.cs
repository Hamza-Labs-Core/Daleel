using System.Text.Json;
using Daleel.Core.Llm;
using Daleel.Core.Observability;
using Daleel.Search.Http;

namespace Daleel.Agent.Llm;

/// <summary>
/// <see cref="ILlmClient"/> backed by OpenRouter (https://openrouter.ai), an LLM gateway that
/// exposes models from every major provider — Anthropic, OpenAI, Google, Meta, Mistral, … —
/// behind a single OpenAI-compatible chat-completions endpoint. One API key reaches them all,
/// and the wire format is identical to OpenAI's, so this client mirrors <see cref="OpenAiClient"/>.
/// </summary>
/// <remarks>
/// The only differences from the OpenAI client are: the base URL, the bearer key sourced from
/// <c>OPENROUTER_API_KEY</c>, and the optional <c>HTTP-Referer</c> / <c>X-Title</c> attribution
/// headers OpenRouter uses to identify the calling app on its dashboards and rankings.
/// </remarks>
public sealed class OpenRouterClient : HttpProviderBase, ILlmClient
{
    public const string DefaultBaseUrl = "https://openrouter.ai";

    /// <summary>
    /// Default model: a strong, broadly-available model with good Arabic competence. Override
    /// per call (e.g. <c>google/gemini-2.5-flash</c> for cheaper/faster runs) via the CLI.
    /// </summary>
    public const string DefaultModel = "moonshotai/kimi-k2.7-code:nitro";

    private const string DefaultReferer = "https://github.com/Hamza-Labs-Core/Daleel";
    private const string DefaultTitle = "Daleel";

    /// <summary>
    /// Hard per-call timeout for a single chat-completions attempt. A hung gateway/model must not be
    /// able to stall the pipeline indefinitely — this bounds every attempt deterministically, above
    /// and beyond the coarser <see cref="HttpClient.Timeout"/> backstop.
    /// </summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _referer;
    private readonly string _title;

    public string Provider => "openrouter";
    protected override string ProviderName => Provider;

    /// <summary>The model id this client sends to OpenRouter (e.g. <c>anthropic/claude-sonnet-4</c>).</summary>
    public string Model => _model;

    public OpenRouterClient(
        string? apiKey = null,
        string? model = DefaultModel,
        string referer = DefaultReferer,
        string title = DefaultTitle,
        HttpClient? httpClient = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : base(ConfigureClient(httpClient), maxRetries: 2, delay, perAttemptTimeout: CallTimeout)
    {
        _apiKey = apiKey
                  ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
                  ?? throw new ProviderException("OPENROUTER_API_KEY is not set.");
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        _referer = referer;
        _title = title;
    }

    private static HttpClient ConfigureClient(HttpClient? client)
    {
        if (client is null)
        {
            client = SharedHttpHandler.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(2);
        }

        client.BaseAddress ??= new Uri(DefaultBaseUrl);
        return client;
    }

    public async Task<LlmResponse> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<LlmMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var apiMessages = new List<object> { new { role = "system", content = systemPrompt } };
        apiMessages.AddRange(messages.Select(m => (object)new { role = m.Role, content = m.Content }));

        // The ambient search session id becomes OpenRouter's `session_id` — it groups every call a
        // search makes under one identifier for observability AND enables sticky routing (all of a
        // session's calls go to the same provider), which maximizes prompt-cache hits across a search's
        // planner/extraction/crawl/detail/drain calls. Omitted when no search owns this flow (off-search
        // callers) so we never attribute stray calls to a session. (256-char cap; ours is short.)
        var payload = AmbientLlmSession.SessionId is { Length: > 0 } session
            ? (object)new { model = _model, messages = apiMessages, session_id = session }
            : new { model = _model, messages = apiMessages };

        using var doc = await SendJsonAsync(
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/completions") { Content = JsonBody(payload) };
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
                // OpenRouter attribution headers — surface the app on its dashboards/rankings.
                req.Headers.Add("HTTP-Referer", _referer);
                req.Headers.Add("X-Title", _title);
                return req;
            },
            cancellationToken).ConfigureAwait(false);

        var root = doc.RootElement;
        var content = ChatCompletionParsing.ExtractContent(root, Provider);

        // OpenRouter echoes the resolved model id; fall back to the requested one if absent.
        var model = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() ?? _model : _model;

        int? inTok = null, outTok = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pv)) inTok = pv;
            if (usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var cv)) outTok = cv;
        }

        return new LlmResponse { Content = content, Model = model, InputTokens = inTok, OutputTokens = outTok };
    }
}
