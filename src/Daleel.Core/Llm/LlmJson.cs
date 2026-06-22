using System.Text.Json;

namespace Daleel.Core.Llm;

/// <summary>
/// Helpers for coaxing structured JSON out of LLM text responses. Models frequently wrap
/// JSON in markdown code fences or add prose around it, so we extract the first balanced
/// JSON value before parsing.
/// </summary>
public static class LlmJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Extracts the first top-level JSON object or array from <paramref name="text"/>,
    /// stripping markdown fences and surrounding prose. Returns null when none is found.
    /// </summary>
    public static string? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var s = text.Trim();

        // Strip a leading ```json / ``` fence and its closing fence if present.
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = s.IndexOf('\n');
            if (firstNewline >= 0)
            {
                s = s[(firstNewline + 1)..];
            }

            var closingFence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
            {
                s = s[..closingFence];
            }

            s = s.Trim();
        }

        // Find the first balanced { } or [ ] span.
        var start = s.IndexOfAny(new[] { '{', '[' });
        if (start < 0)
        {
            return null;
        }

        var open = s[start];
        var close = open == '{' ? '}' : ']';
        var depth = 0;
        var inString = false;
        var escape = false;

        for (var i = start; i < s.Length; i++)
        {
            var ch = s[i];

            if (escape)
            {
                escape = false;
                continue;
            }

            switch (ch)
            {
                case '\\' when inString:
                    escape = true;
                    break;
                case '"':
                    inString = !inString;
                    break;
                default:
                    if (!inString)
                    {
                        if (ch == open) depth++;
                        else if (ch == close && --depth == 0) return s[start..(i + 1)];
                    }
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Deserializes <typeparamref name="T"/> from the JSON embedded in an LLM response.
    /// Returns <c>default</c> when no JSON is present or parsing fails.
    /// </summary>
    public static T? Deserialize<T>(string? llmText)
    {
        var json = ExtractJson(llmText);
        if (json is null)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
