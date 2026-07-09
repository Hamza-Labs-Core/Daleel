namespace Daleel.Web.Data;

/// <summary>Work-item lifecycle. Terminal states are <see cref="Done"/> and <see cref="Dead"/>.</summary>
public static class WorkItemStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Done = "done";

    /// <summary>
    /// Retries exhausted (or a non-retryable give-up like the cost cap). Deliberately visible —
    /// dead items stay queryable with their LastError; nothing is ever silently dropped.
    /// </summary>
    public const string Dead = "dead";
}

/// <summary>
/// One unit of post-result enrichment work: a single API dive for a single result piece (one item's
/// spec scrape, one store's catalogue, one brand harvest…). The table IS the queue — the consumer
/// claims rows with FOR UPDATE SKIP LOCKED (same pattern as <c>SearchJobs</c>), each unit succeeds,
/// retries, or dies alone, and a container restart merely lets claims lease-expire back to pending.
/// Units can enqueue follow-up units, so deep-dives fan out through the queue, never through a
/// monolithic in-process phase with a shared lifetime.
/// </summary>
public class EnrichmentWorkItem
{
    public long Id { get; set; }

    /// <summary>The search job whose result this unit enriches (correlation + patch target).</summary>
    public int SearchJobId { get; set; }

    /// <summary>Job owner — patches update this user's conversation and history.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>History row captured at result insert, so late patches update that exact entry.</summary>
    public int HistoryEntryId { get; set; }

    /// <summary>Result type of the base result (threaded to conversation updates + broadcasts).</summary>
    public string ResultType { get; set; } = string.Empty;

    /// <summary>Unit kind — dispatches to the matching handler (see <c>EnrichmentUnit</c>).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Kind-specific JSON arguments (item index/name, domain, brand…).</summary>
    public string Payload { get; set; } = "{}";

    public string Status { get; set; } = WorkItemStatus.Pending;

    /// <summary>Executions started (incremented at claim). Retry backoff scales on this.</summary>
    public int Attempts { get; set; }

    public int MaxAttempts { get; set; } = 4;

    /// <summary>Earliest eligible claim time — retry backoff and deliberate ordering delays.</summary>
    public DateTimeOffset NotBefore { get; set; }

    /// <summary>
    /// Claim lease. A running row whose lease expired is claimable again — this is the entire
    /// crash-recovery story: no boot reconciler needed, a dead container's items simply re-lease.
    /// </summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>Last failure/retry reason; on a dead row, why it gave up.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
