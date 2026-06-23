using System.Text.Json;
using Daleel.Search.Http;

namespace Daleel.Agent.Llm;

/// <summary>
/// Shared parsing for the OpenAI-compatible chat-completions response shape, used by both
/// <see cref="OpenAiClient"/> and <see cref="OpenRouterClient"/>.
/// </summary>
internal static class ChatCompletionParsing
{
    /// <summary>
    /// Pulls <c>choices[0].message.content</c> out of a chat-completions body, throwing a clear
    /// <see cref="ProviderException"/> instead of an opaque <c>KeyNotFoundException</c>/index error
    /// when the gateway returns an error-shaped 200 (no <c>choices</c>, or an empty array).
    /// </summary>
    public static string ExtractContent(JsonElement root, string provider)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            // Surface the API's own error message when present (e.g. {"error":{"message":"…"}}).
            var detail = root.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg)
                ? msg.GetString()
                : null;
            throw new ProviderException(
                $"{provider}: response contained no choices.{(detail is null ? string.Empty : $" {detail}")}");
        }

        return choices[0].TryGetProperty("message", out var message)
               && message.TryGetProperty("content", out var content)
            ? content.GetString() ?? string.Empty
            : string.Empty;
    }
}
