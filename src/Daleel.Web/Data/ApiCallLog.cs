using System.ComponentModel.DataAnnotations;

namespace Daleel.Web.Data;

/// <summary>
/// A persisted record of one external API call made while running a search job — the basis for
/// per-user usage, per-provider analytics, and cost tracking.
/// </summary>
public sealed class ApiCallLog
{
    public long Id { get; set; }

    /// <summary>Owner of the search that triggered the call.</summary>
    public string? UserId { get; set; }

    /// <summary>Search job the call belongs to.</summary>
    public int? JobId { get; set; }

    [Required]
    public string Provider { get; set; } = string.Empty;

    [Required]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Short, non-sensitive request summary (query/url/model).</summary>
    public string? RequestSummary { get; set; }

    /// <summary>
    /// Short, non-sensitive description of what the call RETURNED ("0 products", "8 products",
    /// "3.7 KB markdown"). Makes a paid-but-useless call visible on the provider-efficiency view —
    /// cost and duration alone cannot tell a productive catalogue crawl from an empty one.
    /// </summary>
    public string? ResponseSummary { get; set; }

    public long ResponseTimeMs { get; set; }
    public long ResponseBytes { get; set; }

    /// <summary>success / error / timeout.</summary>
    public string Status { get; set; } = "success";

    public decimal EstimatedCost { get; set; }

    public string? Model { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
