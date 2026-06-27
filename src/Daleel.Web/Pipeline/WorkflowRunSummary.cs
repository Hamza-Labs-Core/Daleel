namespace Daleel.Web.Pipeline;

/// <summary>
/// A compact, JSON-serializable snapshot of a finished search run, stamped into the Elsa workflow
/// instance's <c>WorkflowState.Properties</c> under <see cref="PropertyKey"/> before it is persisted.
/// The admin workflows page reads it back off each persisted instance so it can show what the run did
/// (query, market, outcome, timing) without re-deserializing the full result payload.
/// </summary>
/// <remarks>
/// Stored as a serialized string rather than the whole <see cref="SearchPipelineState"/>: the state's
/// large nested object graph (bundle, products, answer) isn't needed for the admin list and keeps the
/// persisted instance small. Everything here is a primitive or string, so it round-trips cleanly through
/// Elsa's property serializer regardless of the configured persistence provider.
/// </remarks>
public sealed record WorkflowRunSummary(
    string? SearchId,
    string Query,
    string Geo,
    string Language,
    string ResultType,
    int ResultCount,
    int FilteredCount,
    bool FromCache,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt)
{
    /// <summary>The <c>WorkflowState.Properties</c> key the summary is stored under.</summary>
    public const string PropertyKey = "daleel:summary";

    /// <summary>Projects the finished pipeline state into the persisted summary.</summary>
    public static WorkflowRunSummary From(SearchPipelineState state) => new(
        state.SearchId,
        state.Query,
        state.Geo,
        state.Language,
        state.ResultType,
        state.ResultCount,
        state.FilteredCount,
        state.FromCache,
        state.StartedAt,
        state.CompletedAt);
}
