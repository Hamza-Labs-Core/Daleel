namespace Daleel.Web.Events;

/// <summary>
/// The admin timeline's filter + paging request. Time, correlation id and user hash are pushed to the
/// database (they're indexed); category / severity / free-text are applied in memory by the pure
/// <see cref="SystemEventFilters"/> over the windowed rows — the same "DB for the window, pure function
/// for the logic" split the usage report uses, so the filter semantics stay unit-testable.
/// </summary>
public sealed record SystemEventQuery
{
    /// <summary>Inclusive lower time bound (UTC). Null reaches back to the window cap.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Exclusive upper time bound (UTC). Null means "up to now".</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Categories to include (any-of). Null or empty means all categories.</summary>
    public IReadOnlyCollection<string>? Categories { get; init; }

    /// <summary>Severities to include (any-of). Null or empty means all severities.</summary>
    public IReadOnlyCollection<string>? Severities { get; init; }

    /// <summary>Case-insensitive substring matched against summary / type / source / details / correlation id.</summary>
    public string? Text { get; init; }

    /// <summary>Exact workflow / search id to scope to (the "all events for one run" drill-down).</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Exact hashed user id to scope to (already hashed — never a raw id).</summary>
    public string? UserHash { get; init; }

    /// <summary>1-based page number.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Rows per page (clamped by the store).</summary>
    public int PageSize { get; init; } = 50;
}

/// <summary>One page of timeline rows plus the total count of rows matching the filter (for the pager).</summary>
public sealed record SystemEventPage(IReadOnlyList<SystemEvent> Items, int Total, int Page, int PageSize)
{
    public static SystemEventPage Empty(SystemEventQuery q) =>
        new(Array.Empty<SystemEvent>(), 0, q.Page, q.PageSize);
}

/// <summary>
/// The pure, DB-free predicate logic behind <see cref="SystemEventQuery"/>: category / severity / free-text
/// matching plus pagination. Kept separate and static so the live store is a thin shell over tested logic.
/// </summary>
public static class SystemEventFilters
{
    /// <summary>Applies the in-memory filters (category, severity, free text) to a sequence of events.</summary>
    public static IEnumerable<SystemEvent> Apply(IEnumerable<SystemEvent> events, SystemEventQuery q)
    {
        var categories = Normalize(q.Categories);
        var severities = Normalize(q.Severities);
        var text = string.IsNullOrWhiteSpace(q.Text) ? null : q.Text.Trim();

        foreach (var e in events)
        {
            if (categories is not null && !categories.Contains(e.Category))
            {
                continue;
            }
            if (severities is not null && !severities.Contains(e.Severity))
            {
                continue;
            }
            if (text is not null && !MatchesText(e, text))
            {
                continue;
            }
            yield return e;
        }
    }

    /// <summary>Takes the <paramref name="q"/>-th page (1-based) of an already-ordered, already-filtered sequence.</summary>
    public static IReadOnlyList<SystemEvent> Page(IEnumerable<SystemEvent> events, SystemEventQuery q)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 500);
        return events.Skip((page - 1) * size).Take(size).ToList();
    }

    /// <summary>True when the free-text term appears (case-insensitively) in any human-facing field.</summary>
    private static bool MatchesText(SystemEvent e, string text) =>
        Contains(e.Summary, text)
        || Contains(e.EventType, text)
        || Contains(e.Source, text)
        || Contains(e.Category, text)
        || Contains(e.CorrelationId, text)
        || Contains(e.DetailsJson, text);

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lower-cases a filter set for case-insensitive any-of matching; null when the set is empty.</summary>
    private static HashSet<string>? Normalize(IReadOnlyCollection<string>? values) =>
        values is null || values.Count == 0
            ? null
            : values.Select(v => v.ToLowerInvariant()).ToHashSet();
}
