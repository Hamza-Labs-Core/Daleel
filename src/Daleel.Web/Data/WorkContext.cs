namespace Daleel.Web.Data;

/// <summary>Scope discriminator for a <see cref="WorkContext"/> row — mirrors WorkItemStatus's const-class shape.</summary>
public static class WorkContextScope
{
    /// <summary>The whole search: one row per job (Key is empty).</summary>
    public const string Search = "search";

    /// <summary>One product: Key is the ProductModel.Id StableId ("p_&lt;8hex&gt;").</summary>
    public const string Product = "product";

    /// <summary>One brand: Key is Brand.Normalize(name) (the NameKey the Brand row upserts on).</summary>
    public const string Brand = "brand";
}

/// <summary>
/// The "big context id tracking the workflow" at three scopes (search / product / brand), one
/// polymorphic row per <c>(SearchJobId, Scope, Key)</c>. It holds an append-only <em>findings
/// ledger</em> that producing enrichment units add to as they land, plus the LATEST LLM
/// <em>synthesis</em> for that entity. It stores NO typed product/brand attributes (that data lives
/// in the R2 entity doc / result answer, never in columns here) — only advisory findings and
/// synthesis text. The synthesis is mirrored onto an existing result field for the UI; this row is
/// the durable log + idempotency ledger that makes the sense-making step crash-safe and re-run-free.
/// </summary>
public sealed class WorkContext
{
    public long Id { get; set; }

    /// <summary>Owning search job — correlation, patch target, prune key. Indexed.</summary>
    public int SearchJobId { get; set; }

    /// <summary>One of <see cref="WorkContextScope"/>: "search" | "product" | "brand".</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>
    /// Entity identity WITHIN the job: search = "" (one per job); product = ProductModel.Id
    /// ("p_&lt;8hex&gt;" StableId, the /product/&lt;id&gt; route key); brand = Brand.Normalize(name)
    /// (the NameKey the Brand row upserts on). Deterministic, so a re-run re-locates the same row.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Append-only JSON array of compact findings, e.g. <c>[{"step":"verifypage","note":"page
    /// marker=new; related=true","at":1720000000000}]</c>. Advisory context for the reducer — a lost
    /// append only weakens the narrative, never corrupts data. Capacity-bounded (last N kept) on append.
    /// </summary>
    public string FindingsJson { get; set; } = "[]";

    /// <summary>The latest reduction the LLM wrote for this scope. Null until synthesized.</summary>
    public string? Synthesis { get; set; }

    /// <summary>
    /// High-water mark: the finding count folded into the current <see cref="Synthesis"/>. A re-run
    /// whose ledger hasn't grown past this skips the LLM call for this scope (re-run-is-free).
    /// </summary>
    public int SynthesizedFindingCount { get; set; }

    /// <summary>Bumped each time <see cref="Synthesis"/> is (re)written — monotonic; keeps surfacing idempotent.</summary>
    public int SynthesisVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SynthesizedAt { get; set; }
}
