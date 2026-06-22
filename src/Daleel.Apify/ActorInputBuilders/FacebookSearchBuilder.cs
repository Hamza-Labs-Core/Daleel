using System.Text.Json;
using System.Text.Json.Nodes;
using Daleel.Core.Models;

namespace Daleel.Apify.ActorInputBuilders;

/// <summary>
/// Builds the input payload for a Facebook <em>search</em> actor
/// (e.g. <c>scrapeforge/facebook-search-posts</c>).
/// </summary>
/// <remarks>
/// Apify actors are configured by a free-form JSON "input" object whose schema is
/// actor-specific. We build a sensible default here but allow a caller-supplied
/// override file to fully replace it, so the tool keeps working if an actor's schema
/// changes without a code change.
/// </remarks>
public static class FacebookSearchBuilder
{
    /// <summary>
    /// Builds the actor input for searching posts by <paramref name="keyword"/>.
    /// When <paramref name="overrideJson"/> is provided it is parsed and returned as-is.
    /// </summary>
    public static JsonNode Build(string keyword, int maxItems, string? overrideJson = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideJson))
        {
            return JsonNode.Parse(overrideJson)
                   ?? throw new JsonException("Override input parsed to null.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);

        return new JsonObject
        {
            ["query"] = keyword,
            ["searchQuery"] = keyword, // some actor variants use this key
            ["maxItems"] = maxItems,
            ["maxPosts"] = maxItems,
            ["resultsLimit"] = maxItems
        };
    }
}
