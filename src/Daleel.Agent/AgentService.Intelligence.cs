using System.Text.Json.Serialization;
using Daleel.Core.Geo;
using Daleel.Core.Intelligence;
using Daleel.Core.Llm;

namespace Daleel.Agent;

/// <summary>
/// The "thinking" projection of <see cref="AgentService"/>: a single up-front LLM call that reasons
/// about a product category BEFORE any sources are gathered, producing a <see cref="SearchIntelligence"/>
/// (product type, relevant store types, expected brands, the comparison <see cref="ProductSchema"/>, and
/// a price expectation). The pipeline threads this through so extraction, profile relevance and the
/// compare table all know what they are looking for instead of working blind.
/// </summary>
public sealed partial class AgentService
{
    /// <summary>
    /// Asks the LLM to analyse a product category for a market and return the intelligence that
    /// shapes the rest of the search. Never throws: returns <see cref="SearchIntelligence.Neutral"/>
    /// on a thin category, unparseable JSON, or any provider failure, so the pipeline degrades
    /// gracefully to its existing schema-less behaviour.
    /// </summary>
    public async Task<SearchIntelligence> AnalyzeCategoryAsync(
        string category, GeoProfile geo, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return SearchIntelligence.Neutral(category ?? string.Empty);
        }

        try
        {
            var text = await _llm.CompleteTextAsync(
                PromptTemplates.CategoryIntelligenceSystem,
                PromptTemplates.CategoryIntelligence(category, geo),
                cancellationToken).ConfigureAwait(false);

            var dto = LlmJson.Deserialize<CategoryIntelligenceDto>(text);
            return dto is null ? SearchIntelligence.Neutral(category) : MapIntelligence(category, dto);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"category intelligence failed: {ex.Message}");
            return SearchIntelligence.Neutral(category);
        }
    }

    /// <summary>Maps the LLM wire shape into the domain <see cref="SearchIntelligence"/>.</summary>
    private static SearchIntelligence MapIntelligence(string category, CategoryIntelligenceDto dto)
    {
        var productType = string.IsNullOrWhiteSpace(dto.ProductType)
            ? "general"
            : dto.ProductType!.Trim().ToLowerInvariant();

        var fields = (dto.Specs ?? new List<SpecFieldDto>())
            .Where(s => !string.IsNullOrWhiteSpace(s.Key) && !string.IsNullOrWhiteSpace(s.Label))
            .Select(s => new SpecField
            {
                Key = NormalizeSpecKey(s.Key!),
                Label = s.Label!.Trim(),
                Unit = string.IsNullOrWhiteSpace(s.Unit) ? null : s.Unit!.Trim(),
                HigherIsBetter = s.HigherIsBetter,
                Importance = string.Equals(s.Importance?.Trim(), "key", StringComparison.OrdinalIgnoreCase)
                    ? SpecImportance.Key
                    : SpecImportance.Normal
            })
            .GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var schema = fields.Count > 0
            ? new ProductSchema { ProductType = productType, Fields = fields }
            : ProductSchema.General with { ProductType = productType };

        return new SearchIntelligence
        {
            Category = category,
            ProductType = productType,
            RelevantStoreTypes = CleanList(dto.RelevantStoreTypes),
            ExpectedBrands = CleanList(dto.ExpectedBrands),
            Schema = schema,
            PriceExpectation = string.IsNullOrWhiteSpace(dto.PriceExpectation) ? null : dto.PriceExpectation!.Trim(),
            ImagesMatter = dto.ImagesMatter ?? true,
            Reasoning = string.IsNullOrWhiteSpace(dto.Reasoning) ? null : dto.Reasoning!.Trim()
        };
    }

    /// <summary>
    /// Normalises an LLM-supplied spec key to the documented lower_snake_case shape: lowercased,
    /// with every run of non-alphanumeric characters (spaces, hyphens, slashes, punctuation) collapsed
    /// to a single underscore and surrounding underscores trimmed. Keeps schema-aware extraction and
    /// compare keys consistent regardless of how the model formats them (e.g. "Screen-Size", "screen size"
    /// and "screen/size" all map to "screen_size").
    /// </summary>
    private static string NormalizeSpecKey(string key)
    {
        var sanitized = key.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();
        return string.Join('_', new string(sanitized).Split('_', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Wire shape for the category-intelligence LLM output.</summary>
    private sealed class CategoryIntelligenceDto
    {
        [JsonPropertyName("productType")] public string? ProductType { get; set; }
        [JsonPropertyName("relevantStoreTypes")] public List<string>? RelevantStoreTypes { get; set; }
        [JsonPropertyName("expectedBrands")] public List<string>? ExpectedBrands { get; set; }
        [JsonPropertyName("priceExpectation")] public string? PriceExpectation { get; set; }
        [JsonPropertyName("imagesMatter")] public bool? ImagesMatter { get; set; }
        [JsonPropertyName("specs")] public List<SpecFieldDto>? Specs { get; set; }
        [JsonPropertyName("reasoning")] public string? Reasoning { get; set; }
    }

    private sealed class SpecFieldDto
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("unit")] public string? Unit { get; set; }
        [JsonPropertyName("higherIsBetter")] public bool? HigherIsBetter { get; set; }
        [JsonPropertyName("importance")] public string? Importance { get; set; }
    }
}
