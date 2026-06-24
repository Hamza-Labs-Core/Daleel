using System.Text.Json;

namespace Daleel.Search.Abstractions;

/// <summary>
/// Extracts structured data from a web page against a JSON Schema (Context.dev's AI
/// Extract). Split out from <see cref="IScrapeProvider"/> because not every scraper can
/// do schema-driven extraction — callers feature-detect by checking for this interface.
/// </summary>
public interface IExtractProvider
{
    string Name { get; }

    /// <summary>
    /// Extracts data from <paramref name="url"/> against <paramref name="jsonSchema"/> and
    /// returns the extracted object as a cloned <see cref="JsonElement"/>.
    /// </summary>
    Task<JsonElement> ExtractAsync(string url, object jsonSchema, CancellationToken cancellationToken = default);
}
