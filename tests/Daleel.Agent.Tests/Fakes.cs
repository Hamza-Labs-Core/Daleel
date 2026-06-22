using System.Net;
using System.Text;
using Daleel.Core.Llm;
using Daleel.Search.Abstractions;

namespace Daleel.Agent.Tests;

/// <summary>Deterministic LLM client: routes responses by system prompt.</summary>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly Func<string, string> _bySystemPrompt;
    public string Provider => "fake";
    public List<string> SystemPromptsSeen { get; } = new();

    public FakeLlmClient(Func<string, string> bySystemPrompt) => _bySystemPrompt = bySystemPrompt;

    public Task<LlmResponse> CompleteAsync(
        string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default)
    {
        SystemPromptsSeen.Add(systemPrompt);
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
        return Task.FromResult(new SearchResults
        {
            Provider = Name, Query = query.Query, Kind = query.Kind, Results = _results
        });
    }
}

/// <summary>Canned-response HTTP handler for LLM client tests.</summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly string _body;
    public StubHttpMessageHandler(string body) => _body = body;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json")
        });

    public HttpClient Client(string baseUrl) => new(this) { BaseAddress = new Uri(baseUrl) };
}
