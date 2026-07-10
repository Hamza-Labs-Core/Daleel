using System.Net;
using System.Text;
using Daleel.Core.Llm;
using Daleel.Search.Abstractions;

namespace Daleel.Agent.Tests;

/// <summary>Deterministic LLM client: routes responses by system prompt.</summary>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly Func<string, string> _bySystemPrompt;
    private readonly Action<string>? _onUserPrompt;
    public string Provider => "fake";
    public List<string> SystemPromptsSeen { get; } = new();

    public FakeLlmClient(Func<string, string> bySystemPrompt, Action<string>? onUserPrompt = null)
        => (_bySystemPrompt, _onUserPrompt) = (bySystemPrompt, onUserPrompt);

    public Task<LlmResponse> CompleteAsync(
        string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default)
    {
        SystemPromptsSeen.Add(systemPrompt);
        foreach (var m in messages)
        {
            _onUserPrompt?.Invoke(m.Content);
        }
        return Task.FromResult(new LlmResponse { Content = _bySystemPrompt(systemPrompt) });
    }
}

/// <summary>Fake search engine returning canned results for any query/kind.</summary>
public sealed class FakeSearchProvider : ISearchProvider
{
    private readonly IReadOnlyList<SearchResult> _results;
    public string Name => "fake-search";
    public int CallCount { get; private set; }
    public FakeSearchProvider(params SearchResult[] results) => _results = results;
    public bool Supports(SearchKind kind) => true;

    public Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        CallCount++;
        // Return only results whose kind matches the requested kind, so web and shopping
        // results don't bleed into each other (closer to how a real provider behaves).
        var matching = _results.Where(r => r.Kind == query.Kind).ToList();
        return Task.FromResult(new SearchResults
        {
            Provider = Name, Query = query.Query, Kind = query.Kind, Results = matching
        });
    }
}

/// <summary>
/// Canned-response HTTP handler for LLM client tests. Records the last request it saw
/// (and its serialized body) so tests can assert on the outgoing request shape — URL,
/// headers, and JSON payload.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly string _body;
    public StubHttpMessageHandler(string body) => _body = body;

    /// <summary>The most recent request this handler received (null until first call).</summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>The serialized body of the most recent request (empty when there was none).</summary>
    public string LastRequestBody { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json")
        };
    }

    public HttpClient Client(string baseUrl) => new(this) { BaseAddress = new Uri(baseUrl) };
}
