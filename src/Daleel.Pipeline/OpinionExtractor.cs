using System.Text;
using System.Text.Json.Serialization;
using Daleel.Core.Llm;
using Daleel.Core.Models;

namespace Daleel.Pipeline;

/// <summary>
/// LLM-powered extraction of structured <see cref="CustomerOpinion"/>s from raw social
/// posts and forum threads. The matcher/normalizer find <em>relevant</em> text; this turns
/// that text into structured opinion data (subject, sentiment, pros, cons) the report can
/// aggregate.
/// </summary>
/// <remarks>
/// The LLM is asked to return a strict JSON array. Responses are parsed leniently via
/// <see cref="LlmJson"/> (tolerating code fences / prose), and any malformed item is
/// skipped rather than failing the whole batch. Because it depends only on
/// <see cref="ILlmClient"/>, it is unit-testable with a fake client returning canned JSON.
/// </remarks>
public sealed class OpinionExtractor
{
    private readonly ILlmClient _llm;

    public OpinionExtractor(ILlmClient llm) => _llm = llm ?? throw new ArgumentNullException(nameof(llm));

    private const string SystemPrompt =
        "You are an Arabic-and-English market-research analyst. You read social media posts " +
        "and forum messages about products and brands, and extract structured customer opinions. " +
        "You understand Jordanian, Gulf, and Egyptian Arabic dialects. Always respond with a " +
        "JSON array only — no prose, no code fences.";

    /// <summary>
    /// Extracts opinions about <paramref name="subject"/> from a batch of text fragments
    /// (posts, comments, thread excerpts).
    /// </summary>
    public async Task<IReadOnlyList<CustomerOpinion>> ExtractAsync(
        string subject,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        if (texts is null || texts.Count == 0)
        {
            return Array.Empty<CustomerOpinion>();
        }

        var prompt = BuildPrompt(subject, texts);
        var response = await _llm.CompleteTextAsync(SystemPrompt, prompt, cancellationToken).ConfigureAwait(false);

        var dtos = LlmJson.Deserialize<List<OpinionDto>>(response) ?? new List<OpinionDto>();
        var opinions = new List<CustomerOpinion>(dtos.Count);

        foreach (var dto in dtos)
        {
            if (dto is null || string.IsNullOrWhiteSpace(dto.Subject) && string.IsNullOrWhiteSpace(dto.Excerpt))
            {
                continue;
            }

            opinions.Add(new CustomerOpinion
            {
                Subject = string.IsNullOrWhiteSpace(dto.Subject) ? subject : dto.Subject!,
                Sentiment = ParseSentiment(dto.Sentiment),
                Rating = dto.Rating,
                Pros = dto.Pros ?? new List<string>(),
                Cons = dto.Cons ?? new List<string>(),
                Excerpt = dto.Excerpt,
                Source = dto.Source,
                Language = dto.Language
            });
        }

        return opinions;
    }

    private static string BuildPrompt(string subject, IReadOnlyList<string> texts)
    {
        var sb = new StringBuilder();
        sb.Append("Extract customer opinions about \"").Append(subject).AppendLine("\" from the posts below.");
        sb.AppendLine("For each post that expresses an opinion, output an object with fields:");
        sb.AppendLine("  subject (the specific product/model mentioned), sentiment (positive|neutral|negative),");
        sb.AppendLine("  rating (1-5 number or null), pros (string[]), cons (string[]), excerpt (short quote),");
        sb.AppendLine("  language (\"ar\" or \"en\"). Skip posts with no real opinion.");
        sb.AppendLine("Return a JSON array. Posts:");
        sb.AppendLine();

        for (var i = 0; i < texts.Count; i++)
        {
            sb.Append('[').Append(i + 1).Append("] ").AppendLine(texts[i]);
        }

        return sb.ToString();
    }

    private static Sentiment ParseSentiment(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "positive" or "pos" or "ايجابي" or "إيجابي" => Sentiment.Positive,
        "negative" or "neg" or "سلبي" => Sentiment.Negative,
        _ => Sentiment.Neutral
    };

    /// <summary>Wire shape the LLM is asked to produce.</summary>
    private sealed class OpinionDto
    {
        [JsonPropertyName("subject")] public string? Subject { get; set; }
        [JsonPropertyName("sentiment")] public string? Sentiment { get; set; }
        [JsonPropertyName("rating")] public double? Rating { get; set; }
        [JsonPropertyName("pros")] public List<string>? Pros { get; set; }
        [JsonPropertyName("cons")] public List<string>? Cons { get; set; }
        [JsonPropertyName("excerpt")] public string? Excerpt { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("language")] public string? Language { get; set; }
    }
}
