using System.Text.Json;
using Daleel.Core.Observability;

namespace Daleel.Web.Events;

/// <summary>
/// Pure mapping from the existing <see cref="ApiCall"/> telemetry (emitted by every wrapped provider)
/// into a categorized <see cref="PipelineEvent"/>. Keeping it pure and static makes the
/// category-inference rules unit-testable without a database or live providers.
/// </summary>
public static class PipelineEventFactory
{
    /// <summary>Projects a recorded API call into an event row tagged with the owning search id.</summary>
    public static PipelineEvent FromApiCall(ApiCall call, string? searchId) => new()
    {
        Timestamp = call.Timestamp,
        Category = CategoryOf(call.Provider, call.Endpoint),
        EventType = call.Endpoint,
        Provider = call.Provider,
        SearchId = searchId,
        DurationMs = call.ResponseTimeMs,
        EstimatedCost = call.EstimatedCost,
        Success = call.Status == ApiCallStatus.Success,
        MetadataJson = Metadata(call)
    };

    /// <summary>Builds a non-provider event (cache hit/miss, profile lookup) with arbitrary metadata.</summary>
    public static PipelineEvent Custom(
        string category, string eventType, string provider, string? searchId,
        bool success = true, decimal cost = 0m, long durationMs = 0,
        IReadOnlyDictionary<string, object?>? metadata = null) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Category = category,
        EventType = eventType,
        Provider = provider,
        SearchId = searchId,
        DurationMs = durationMs,
        EstimatedCost = cost,
        Success = success,
        MetadataJson = metadata is null ? "{}" : JsonSerializer.Serialize(metadata)
    };

    /// <summary>
    /// Maps a provider+endpoint onto one of the fixed <see cref="EventCategory"/> buckets. Mirrors the
    /// shape <see cref="CostEstimator.EstimateCall"/> keys off, so cost and category stay consistent.
    /// </summary>
    public static string CategoryOf(string provider, string endpoint)
    {
        var p = provider.ToLowerInvariant();
        var e = endpoint.ToLowerInvariant();

        if (p.Contains("openrouter") || p.Contains("openai") || p.Contains("anthropic") || e == "chat")
        {
            return EventCategory.Llm;
        }
        if (p.Contains("places"))
        {
            return EventCategory.Places;
        }
        if (p.Contains("context"))
        {
            return e.Contains("extract") ? EventCategory.Extract : EventCategory.Scrape;
        }
        if (p.Contains("cloudflare") || e.Contains("scrape") || e.Contains("render"))
        {
            return EventCategory.Scrape;
        }
        // SerpAPI / Bing / Apify-social all map to the general search bucket.
        return EventCategory.Search;
    }

    private static string Metadata(ApiCall call)
    {
        var meta = new Dictionary<string, object?>
        {
            ["summary"] = call.RequestSummary,
            ["bytes"] = call.ResponseBytes
        };
        if (call.Model is not null)
        {
            meta["model"] = call.Model;
        }
        if (call.InputTokens is not null || call.OutputTokens is not null)
        {
            meta["inputTokens"] = call.InputTokens;
            meta["outputTokens"] = call.OutputTokens;
        }
        if (call.Status != ApiCallStatus.Success)
        {
            meta["status"] = call.Status.ToString().ToLowerInvariant();
        }
        return JsonSerializer.Serialize(meta);
    }
}
