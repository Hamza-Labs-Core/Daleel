using System.Text.Json;
using Daleel.Core.Llm;
using Daleel.Search.Http;

namespace Daleel.Agent.Llm;

/// <summary>
/// <see cref="ILlmClient"/> backed by the Anthropic Messages API. Anthropic takes the
/// system prompt as a top-level field (not a message) and requires an explicit
/// <c>max_tokens</c> and the <c>anthropic-version</c> header.
/// </summary>
public sealed class AnthropicClient : HttpProviderBase, ILlmClient
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";
    private const string ApiVersion = "2023-06-01";

    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _maxTokens;

    public string Provider => "anthropic";
    protected override string ProviderName => Provider;

    public AnthropicClient(
        string? apiKey = null,
        string model = "claude-opus-4-8",
        int maxTokens = 4096,
        HttpClient? httpClient = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : base(ConfigureClient(httpClient), maxRetries: 2, delay)
    {
        _apiKey = apiKey
                  ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                  ?? throw new ProviderException("ANTHROPIC_API_KEY is not set.");
        _model = model;
        _maxTokens = maxTokens;
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
        var payload = new
        {
            model = _model,
            max_tokens = _maxTokens,
            system = systemPrompt,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray()
        };

        using var doc = await SendJsonAsync(
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = JsonBody(payload) };
                req.Headers.Add("x-api-key", _apiKey);
                req.Headers.Add("anthropic-version", ApiVersion);
                return req;
            },
            cancellationToken).ConfigureAwait(false);

        var root = doc.RootElement;

        // content is an array of blocks; concatenate the text blocks.
        var sb = new System.Text.StringBuilder();
        if (root.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in blocks.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                    block.TryGetProperty("text", out var txt))
                {
                    sb.Append(txt.GetString());
                }
            }
        }

        int? inTok = null, outTok = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var p) && p.TryGetInt32(out var pv)) inTok = pv;
            if (usage.TryGetProperty("output_tokens", out var c) && c.TryGetInt32(out var cv)) outTok = cv;
        }

        return new LlmResponse { Content = sb.ToString(), Model = _model, InputTokens = inTok, OutputTokens = outTok };
    }
}
