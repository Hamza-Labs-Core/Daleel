using System.Text.Json;
using Daleel.Agent.Llm;
using Daleel.Core.Llm;
using FluentAssertions;
using Xunit;

namespace Daleel.Agent.Tests;

public class LlmClientTests
{
    [Fact]
    public async Task AnthropicClient_ParsesTextBlocksAndUsage()
    {
        const string body = """
        {
          "content": [
            { "type": "text", "text": "Hello " },
            { "type": "text", "text": "world" }
          ],
          "usage": { "input_tokens": 10, "output_tokens": 5 }
        }
        """;
        var handler = new StubHttpMessageHandler(body);
        var client = new AnthropicClient(
            apiKey: "k", httpClient: handler.Client(AnthropicClient.DefaultBaseUrl), delay: (_, _) => Task.CompletedTask);

        var response = await client.CompleteAsync("sys", new[] { LlmMessage.User("hi") });

        response.Content.Should().Be("Hello world");
        response.InputTokens.Should().Be(10);
        response.OutputTokens.Should().Be(5);
        client.Provider.Should().Be("anthropic");
    }

    [Fact]
    public async Task OpenAiClient_ParsesChoiceAndUsage()
    {
        const string body = """
        {
          "choices": [ { "message": { "role": "assistant", "content": "42" } } ],
          "usage": { "prompt_tokens": 7, "completion_tokens": 1 }
        }
        """;
        var handler = new StubHttpMessageHandler(body);
        var client = new OpenAiClient(
            apiKey: "k", httpClient: handler.Client(OpenAiClient.DefaultBaseUrl), delay: (_, _) => Task.CompletedTask);

        var response = await client.CompleteAsync("sys", new[] { LlmMessage.User("hi") });

        response.Content.Should().Be("42");
        response.InputTokens.Should().Be(7);
        response.OutputTokens.Should().Be(1);
        client.Provider.Should().Be("openai");
    }

    [Fact]
    public async Task OpenRouterClient_ParsesChoiceUsageAndModel()
    {
        const string body = """
        {
          "model": "anthropic/claude-sonnet-4",
          "choices": [ { "message": { "role": "assistant", "content": "مرحبا" } } ],
          "usage": { "prompt_tokens": 11, "completion_tokens": 3 }
        }
        """;
        var handler = new StubHttpMessageHandler(body);
        var client = new OpenRouterClient(
            apiKey: "k", httpClient: handler.Client(OpenRouterClient.DefaultBaseUrl), delay: (_, _) => Task.CompletedTask);

        var response = await client.CompleteAsync("sys", new[] { LlmMessage.User("hi") });

        response.Content.Should().Be("مرحبا");
        response.InputTokens.Should().Be(11);
        response.OutputTokens.Should().Be(3);
        response.Model.Should().Be("anthropic/claude-sonnet-4");
        client.Provider.Should().Be("openrouter");
    }

    [Fact]
    public async Task OpenRouterClient_SendsOpenAiCompatibleRequestWithAttributionHeaders()
    {
        const string body = """{ "choices": [ { "message": { "content": "ok" } } ] }""";
        var handler = new StubHttpMessageHandler(body);
        var client = new OpenRouterClient(
            apiKey: "secret-key",
            model: "google/gemini-2.5-flash",
            httpClient: handler.Client(OpenRouterClient.DefaultBaseUrl),
            delay: (_, _) => Task.CompletedTask);

        await client.CompleteAsync("system prompt", new[] { LlmMessage.User("user prompt") });

        var req = handler.LastRequest!;
        req.Method.Should().Be(HttpMethod.Post);
        req.RequestUri!.ToString().Should().Be("https://openrouter.ai/api/v1/chat/completions");

        req.Headers.Authorization!.Scheme.Should().Be("Bearer");
        req.Headers.Authorization.Parameter.Should().Be("secret-key");
        req.Headers.GetValues("HTTP-Referer").Should().ContainSingle();
        req.Headers.GetValues("X-Title").Should().Equal("Daleel");

        // OpenAI-compatible payload: model + a system message followed by the user turn.
        using var sent = JsonDocument.Parse(handler.LastRequestBody);
        var root = sent.RootElement;
        root.GetProperty("model").GetString().Should().Be("google/gemini-2.5-flash");
        var messages = root.GetProperty("messages");
        messages.GetArrayLength().Should().Be(2);
        messages[0].GetProperty("role").GetString().Should().Be("system");
        messages[0].GetProperty("content").GetString().Should().Be("system prompt");
        messages[1].GetProperty("role").GetString().Should().Be("user");
        messages[1].GetProperty("content").GetString().Should().Be("user prompt");
    }

    [Fact]
    public async Task OpenRouterClient_FallsBackToDefaultModel_WhenNullOrBlank()
    {
        const string body = """{ "choices": [ { "message": { "content": "ok" } } ] }""";
        var handler = new StubHttpMessageHandler(body);
        var client = new OpenRouterClient(
            apiKey: "k", model: "  ",
            httpClient: handler.Client(OpenRouterClient.DefaultBaseUrl), delay: (_, _) => Task.CompletedTask);

        client.Model.Should().Be(OpenRouterClient.DefaultModel);

        await client.CompleteAsync("sys", new[] { LlmMessage.User("hi") });

        using var sent = JsonDocument.Parse(handler.LastRequestBody);
        sent.RootElement.GetProperty("model").GetString().Should().Be(OpenRouterClient.DefaultModel);
    }

    [Fact]
    public async Task CompleteTextAsync_DefaultInterfaceHelper_ReturnsContent()
    {
        const string body = """{ "content": [ { "type": "text", "text": "ok" } ] }""";
        var handler = new StubHttpMessageHandler(body);
        ILlmClient client = new AnthropicClient(
            apiKey: "k", httpClient: handler.Client(AnthropicClient.DefaultBaseUrl), delay: (_, _) => Task.CompletedTask);

        var text = await client.CompleteTextAsync("sys", "user");
        text.Should().Be("ok");
    }
}
