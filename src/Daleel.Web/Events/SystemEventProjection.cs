namespace Daleel.Web.Events;

/// <summary>
/// Pure mapping from the cost-dashboard's provider-shaped <see cref="PipelineEvent"/> firehose into the
/// admin timeline's user-facing <see cref="SystemEvent"/> rows. The pipeline already records every
/// provider call, scrape, LLM completion, cache decision and profile lookup of a search run, so the
/// timeline reuses that stream rather than re-instrumenting twenty activities — it just re-buckets each
/// event into the coarser timeline categories and assigns a severity. Kept pure and static so the
/// re-bucketing rules are unit-testable without a database.
/// </summary>
public static class SystemEventProjection
{
    /// <summary>
    /// Projects one pipeline event into a timeline event, attaching the (already hashed) acting user so
    /// the timeline's per-user filter works for search-correlated activity. Pipeline events carry no
    /// identity of their own, so <paramref name="userHash"/> is supplied by the flush site (which knows
    /// the owning <c>SearchJob.UserId</c>).
    /// </summary>
    public static SystemEvent FromPipelineEvent(PipelineEvent pe, string? userHash) => new()
    {
        Timestamp = pe.Timestamp,
        Category = CategoryOf(pe.Category, pe.EventType),
        EventType = pe.EventType,
        Severity = pe.Success ? SystemEventSeverity.Info : SystemEventSeverity.Error,
        Source = string.IsNullOrWhiteSpace(pe.Provider) ? "pipeline" : $"pipeline/{pe.Provider}",
        Summary = Summarize(pe),
        DetailsJson = string.IsNullOrWhiteSpace(pe.MetadataJson) ? "{}" : pe.MetadataJson,
        CorrelationId = pe.SearchId,
        UserHash = userHash
    };

    /// <summary>
    /// Re-buckets a pipeline (provider-shaped) category + event type into a timeline category. The event
    /// type carries the entity it concerns (e.g. "profile.brand", "store.verify", "item.deepdive"), so it
    /// wins over the coarse provider category when it names a brand/store/item/cache action.
    /// </summary>
    public static string CategoryOf(string pipelineCategory, string eventType)
    {
        var t = (eventType ?? string.Empty).ToLowerInvariant();

        // The event type names the concerned entity directly — most precise, so check it first.
        if (t.Contains("cache")) return SystemEventCategory.Cache;
        if (t.Contains("brand")) return SystemEventCategory.Brand;
        if (t.Contains("store")) return SystemEventCategory.Store;
        if (t.Contains("item")) return SystemEventCategory.Item;
        if (t.Contains("subworkflow") || t.Contains("workflow")) return SystemEventCategory.Workflow;

        // Otherwise fall back to the provider-shaped category from the cost pipeline.
        return (pipelineCategory ?? string.Empty).ToLowerInvariant() switch
        {
            EventCategory.Llm => SystemEventCategory.Llm,
            EventCategory.Cache => SystemEventCategory.Cache,
            EventCategory.Places => SystemEventCategory.Store,   // places lookups verify a store
            EventCategory.Profile => SystemEventCategory.Item,   // generic profile work is item-level
            EventCategory.Scrape => SystemEventCategory.Item,    // page scrapes feed item deep-dives
            EventCategory.Extract => SystemEventCategory.Item,
            EventCategory.Search => SystemEventCategory.Search,
            _ => SystemEventCategory.Workflow
        };
    }

    /// <summary>A compact "&lt;event type&gt; via &lt;provider&gt;" line, the provider omitted when absent.</summary>
    private static string Summarize(PipelineEvent pe) =>
        string.IsNullOrWhiteSpace(pe.Provider) ? pe.EventType : $"{pe.EventType} · {pe.Provider}";
}
