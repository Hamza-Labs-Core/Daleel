namespace Daleel.Agent;

/// <summary>A previously-flagged-not-relevant example, fed into the relevance gate as advisory calibration.</summary>
public sealed record RelevanceNegative(string Label, string? Reason = null);

/// <summary>
/// DB-free snapshot of the relevance-learning signal for one search — the items shoppers previously flagged
/// as NOT relevant to this query. Passed on the request (exactly like the moderation policy) so AgentService
/// stays DB-blind; the Web layer builds it from the RelevanceFlag store and the search runner attaches it.
/// </summary>
public sealed record RelevancePolicySnapshot(IReadOnlyList<RelevanceNegative> Negatives)
{
    public static readonly RelevancePolicySnapshot Empty = new(Array.Empty<RelevanceNegative>());

    public bool IsEmpty => Negatives.Count == 0;
}
