using System.Text.Json;
using Daleel.Agent.Llm;
using Daleel.Core.Llm;
using Daleel.Core.Observability;
using FluentAssertions;
using Xunit;

namespace Daleel.Agent.Tests;

/// <summary>
/// The ambient search session id (AmbientLlmSession) rides every OpenRouter request as its `user`
/// field, so all of a search's LLM calls group under one identifier at the provider. No session set
/// ⇒ no `user` field (nothing to attribute).
/// </summary>
public class OpenRouterSessionTests
{
    private static (OpenRouterClient Client, Func<string?> LastBody) Build()
    {
        string? body = null;
        var handler = new StubHandler(req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return """{"choices":[{"message":{"content":"ok"}}],"model":"m"}""";
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri(OpenRouterClient.DefaultBaseUrl) };
        return (new OpenRouterClient("key", model: "m", httpClient: http), () => body);
    }

    [Fact]
    public async Task Request_carries_the_ambient_session_as_the_user_field()
    {
        var (client, lastBody) = Build();
        using (AmbientLlmSession.Begin("search-4242"))
        {
            await client.CompleteAsync("sys", new[] { new LlmMessage("user", "hi") });
        }

        using var doc = JsonDocument.Parse(lastBody()!);
        doc.RootElement.GetProperty("user").GetString().Should().Be("search-4242");
    }

    [Fact]
    public async Task Request_omits_user_when_no_session_is_set()
    {
        var (client, lastBody) = Build();
        await client.CompleteAsync("sys", new[] { new LlmMessage("user", "hi") });

        using var doc = JsonDocument.Parse(lastBody()!);
        doc.RootElement.TryGetProperty("user", out _).Should().BeFalse();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), System.Text.Encoding.UTF8, "application/json")
            });
    }
}
