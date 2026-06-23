using System.Text.Json;
using Daleel.Core.Llm;
using Daleel.Search.Http;

namespace Daleel.Agent.Llm;

/// <summary>
/// <see cref="ILlmClient"/> backed by the OpenAI Chat Completions API. Reuses the search
/// layer's <see cref="HttpProviderBase"/> for retry/backoff and JSON plumbing.
/// </summary>
public sealed class OpenAiClient : HttpProviderBase, ILlmClient
{
    public const string DefaultBaseUrl = "https://api.openai.com";

    private readonly string _apiKey;
    private readonly string _model;

    public string Provider => "openai";
    protected override string ProviderName => Provider;

    public OpenAiClient(
        string? apiKey = null,
        string model = "gpt-4o",
        HttpClient? httpClient = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : base(ConfigureClient(httpClient), maxRetries: 2, delay)
    {
        _apiKey = apiKey
                  ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                  ?? throw new ProviderException("OPENAI_API_KEY is not set.");
        _model = model;
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

        var payload = new { model = _model, messages = apiMessages };

        using var doc = await SendJsonAsync(
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = JsonBody(payload) };
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
                return req;
            },
            cancellationToken).ConfigureAwait(false);

        var root = doc.RootElement;
        var content = ChatCompletionParsing.ExtractContent(root, Provider);

        int? inTok = null, outTok = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pv)) inTok = pv;
            if (usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var cv)) outTok = cv;
        }

        return new LlmResponse { Content = content, Model = _model, InputTokens = inTok, OutputTokens = outTok };
    }
}
