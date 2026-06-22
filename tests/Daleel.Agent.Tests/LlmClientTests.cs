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
