namespace Daleel.Core.Observability;

/// <summary>Severity of a <see cref="SearchEvent"/> (maps to the Web layer's SystemEvent severity).</summary>
public enum SearchEventLevel
{
    Info,
    Warning,
    Error
}

/// <summary>
/// One semantic pipeline event during a search — "web discovery returned 42 urls", "extracted 7 products
/// from page X", "browser scrape fell back to Context.dev". Layer-neutral (lives in Core) so AgentService
/// and the search/scrape providers can emit without referencing the Web event store. The Web layer maps it
/// to a persisted SystemEvent stamped with the run's correlation id.
/// </summary>
public readonly record struct SearchEvent(
    string Category,
    string EventType,
    string Summary,
    SearchEventLevel Level = SearchEventLevel.Info,
    string Source = "",
    IReadOnlyDictionary<string, object?>? Details = null);

/// <summary>
/// Receives <see cref="SearchEvent"/>s for the current search. Deliberately synchronous and void so hot
/// paths never await telemetry; the Web implementation enqueues to a bounded channel and returns
/// immediately. Emitters resolve the sink from <see cref="AmbientSearchEvents.Sink"/> and null-check it.
/// </summary>
public interface ISearchEventSink
{
    void Emit(SearchEvent ev);
}

/// <summary>A sink that drops everything — the "no live sink" default for tests, CLI, and startup work.</summary>
public sealed class NullSearchEventSink : ISearchEventSink
{
    public static readonly NullSearchEventSink Instance = new();
    public void Emit(SearchEvent ev) { }
}

/// <summary>
/// Category constants for <see cref="SearchEvent.Category"/> — the SAME lowercase strings the Web layer's
/// SystemEventCategory uses, so a sink can pass Category through to the persisted event unchanged.
/// </summary>
public static class SearchEventCategories
{
    public const string Search = "search";
    public const string Workflow = "workflow";
    public const string Brand = "brand";
    public const string Store = "store";
    public const string Item = "item";
    public const string Cache = "cache";
    public const string Llm = "llm";
    public const string Extract = "extract";
}
