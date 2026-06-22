namespace Daleel.Core.Llm;

/// <summary>One message in an LLM conversation.</summary>
public record LlmMessage(string Role, string Content)
{
    public static LlmMessage User(string content) => new("user", content);
    public static LlmMessage Assistant(string content) => new("assistant", content);
}

/// <summary>The result of an LLM completion.</summary>
public record LlmResponse
{
    public string Content { get; init; } = string.Empty;
    public string? Model { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
}

/// <summary>
/// Provider-agnostic abstraction over a chat-completion LLM. Implementations live in
/// <c>Daleel.Agent</c> (OpenAI, Anthropic); the interface lives in Core so domain code
/// such as the opinion extractor can depend on it without referencing a provider.
/// </summary>
/// <remarks>
/// The abstraction is deliberately small: a single multi-turn completion call plus two
/// convenience helpers. JSON-mode requests are expressed by asking the model for JSON in
/// the prompt and parsing the text response, which keeps the interface provider-neutral.
/// </remarks>
public interface ILlmClient
{
    /// <summary>Identifies the backing provider, e.g. "openai" or "anthropic".</summary>
    string Provider { get; }

    /// <summary>Runs a multi-turn completion.</summary>
    Task<LlmResponse> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<LlmMessage> messages,
        CancellationToken cancellationToken = default);

    /// <summary>Convenience: a single user turn, returning just the text.</summary>
    async Task<string> CompleteTextAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        var response = await CompleteAsync(
            systemPrompt,
            new[] { LlmMessage.User(userPrompt) },
            cancellationToken).ConfigureAwait(false);
        return response.Content;
    }
}
