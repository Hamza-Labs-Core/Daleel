using System.ComponentModel.DataAnnotations;

namespace Daleel.Web.Data;

/// <summary>Lifecycle states for an async search job.</summary>
public static class JobStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// A unit of async search work. Created by POST /api/search, picked up by the background
/// <c>SearchJobService</c>, and streamed to the user's devices over SignalR. The HTTP request never
/// runs the (30–60s) agent itself.
/// </summary>
public sealed class SearchJob
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public string Query { get; set; } = string.Empty;

    public string QueryType { get; set; } = "ask";
    public string Geo { get; set; } = "jordan";
    public string Model { get; set; } = string.Empty;

    public string Status { get; set; } = JobStatus.Queued;
    public string? ProgressMessage { get; set; }
    public string? ResultJson { get; set; }
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// A user's single active conversation (ChatGPT-style: one per user). Replaced on each new search,
/// persists across sessions, and is the source of truth every device renders on load.
/// </summary>
public sealed class UserConversation
{
    [Key]
    public string UserId { get; set; } = string.Empty;

    public int? CurrentJobId { get; set; }
    public string? CurrentQuery { get; set; }

    /// <summary>idle / running / completed / error — mirrors the active job's state.</summary>
    public string CurrentStatus { get; set; } = "idle";

    public string? CurrentResultJson { get; set; }
    public string? CurrentResultType { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
