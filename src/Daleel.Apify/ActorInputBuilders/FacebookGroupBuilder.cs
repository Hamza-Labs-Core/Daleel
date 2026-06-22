using System.Text.Json;
using System.Text.Json.Nodes;

namespace Daleel.Apify.ActorInputBuilders;

/// <summary>
/// Builds the input payload for a Facebook <em>group</em> actor
/// (e.g. <c>apify/facebook-groups-scraper</c>).
/// </summary>
public static class FacebookGroupBuilder
{
    /// <summary>
    /// Builds the actor input for scraping a group identified by <paramref name="groupUrlOrId"/>.
    /// When <paramref name="overrideJson"/> is provided it is parsed and returned as-is.
    /// </summary>
    public static JsonNode Build(string groupUrlOrId, int maxItems, string? keyword = null, string? overrideJson = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideJson))
        {
            return JsonNode.Parse(overrideJson)
                   ?? throw new JsonException("Override input parsed to null.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(groupUrlOrId);

        var startUrls = new JsonArray
        {
            new JsonObject { ["url"] = NormalizeGroupUrl(groupUrlOrId) }
        };

        var input = new JsonObject
        {
            ["startUrls"] = startUrls,
            ["maxItems"] = maxItems,
            ["resultsLimit"] = maxItems
        };

        // Some group actors support a server-side keyword filter; pass it through when set.
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            input["searchQuery"] = keyword;
        }

        return input;
    }

    /// <summary>
    /// Accepts either a bare group id or a full URL and returns a canonical group URL.
    /// </summary>
    private static string NormalizeGroupUrl(string groupUrlOrId)
    {
        if (groupUrlOrId.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return groupUrlOrId;
        }

        return $"https://www.facebook.com/groups/{groupUrlOrId}";
    }
}
