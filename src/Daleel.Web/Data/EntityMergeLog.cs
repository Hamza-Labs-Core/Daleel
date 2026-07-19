using System.ComponentModel.DataAnnotations;

namespace Daleel.Web.Data;

/// <summary>
/// The dedup worker's audit ledger: one row per merge (who survived, who became an alias, on what
/// evidence). Read-only visibility on /admin/data — merges are evidence-driven, never manual — and
/// the undo trail (alias rows keep their old R2Key).
/// </summary>
public sealed class EntityMergeLog
{
    [Key]
    public long Id { get; set; }

    /// <summary>The surviving entity's id.</summary>
    public string SurvivorId { get; set; } = string.Empty;

    /// <summary>The merged-away entity's id (now an alias row).</summary>
    public string LoserId { get; set; } = string.Empty;

    /// <summary>Display names at merge time, for the admin report.</summary>
    public string SurvivorName { get; set; } = string.Empty;
    public string LoserName { get; set; } = string.Empty;

    /// <summary>What decided it: "identity-key" | "product-key" | "vision" | "llm".</summary>
    public string Evidence { get; set; } = string.Empty;

    /// <summary>True when the run was a dry-run (nothing was written; the row records intent).</summary>
    public bool DryRun { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
