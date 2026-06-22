using System.ComponentModel.DataAnnotations;

namespace Daleel.Web.Data;

/// <summary>
/// One row of a user's search history — recorded automatically every time an agent query
/// completes. Holds enough to re-run the query and show a one-line preview, but not the full
/// result blob (that lives in <see cref="SavedResult"/> only when the user explicitly saves).
/// </summary>
public sealed class SearchHistoryEntry
{
    public int Id { get; set; }

    /// <summary>Owner. Every read is filtered by this — see the repository's WHERE clause.</summary>
    [Required]
    public string UserId { get; set; } = string.Empty;

    /// <summary>The raw query the user typed (question, brand, product, etc.).</summary>
    [Required]
    public string Query { get; set; } = string.Empty;

    /// <summary>Which feature ran it: ask/brand/stores/deals/product/compare/reviews.</summary>
    public string QueryType { get; set; } = string.Empty;

    /// <summary>Market key, e.g. "jordan".</summary>
    public string Geo { get; set; } = string.Empty;

    /// <summary>OpenRouter model id used for the run.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Short, human-readable preview of the result (truncated summary).</summary>
    public string? ResultSummary { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Saved results spun off from this search (back-reference; optional).</summary>
    public ICollection<SavedResult> SavedResults { get; set; } = new List<SavedResult>();
}
