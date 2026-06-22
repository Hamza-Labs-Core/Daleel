using System.ComponentModel.DataAnnotations;

namespace Daleel.Web.Data;

/// <summary>
/// A result the user explicitly bookmarked. Stores the full JSON blob of the agent result so it
/// can be re-rendered later without re-running the (paid) query, plus user-supplied title/notes.
/// </summary>
public sealed class SavedResult
{
    public int Id { get; set; }

    /// <summary>Owner. Every read is filtered by this.</summary>
    [Required]
    public string UserId { get; set; } = string.Empty;

    /// <summary>Optional link back to the history entry the result came from.</summary>
    public int? SearchHistoryId { get; set; }
    public SearchHistoryEntry? SearchHistory { get; set; }

    /// <summary>User-facing title (defaults to the query, editable when saving).</summary>
    [Required]
    public string Title { get; set; } = string.Empty;

    /// <summary>The complete serialized result (AgentAnswer, BrandReport, …) as JSON.</summary>
    [Required]
    public string ResultJson { get; set; } = string.Empty;

    /// <summary>Discriminator telling the viewer how to deserialize <see cref="ResultJson"/>.</summary>
    public string ResultType { get; set; } = string.Empty;

    /// <summary>Optional free-text notes the user attached when saving.</summary>
    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
