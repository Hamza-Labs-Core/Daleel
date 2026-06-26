using Daleel.Core.Models;

namespace Daleel.Web.Pipeline.SubWorkflows;

/// <summary>
/// Per-brand state for <see cref="BrandResearchWorkflow"/>. Seeded with the extracted
/// <see cref="BrandInfo"/>; the activities resolve its local website, scrape its catalogue + synthesize
/// a reputation profile via Context.dev + the LLM, persist it, then fold the saved profile back onto
/// <see cref="Result"/> — which the dispatcher reads back into the search's product set.
/// </summary>
public sealed class BrandResearchState : SubWorkflowState
{
    /// <summary>The brand as extracted from the search (input).</summary>
    public BrandInfo Brand { get; set; } = default!;

    /// <summary>The enriched brand (output). Starts equal to <see cref="Brand"/>; folds in the saved profile.</summary>
    public BrandInfo Result { get; set; } = default!;

    /// <summary>The saved profile found in the DB (may be stale).</summary>
    public Data.Brand? Existing { get; set; }

    /// <summary>A freshly-researched profile (null when the saved one was fresh, or research was unavailable).</summary>
    public Data.Brand? Researched { get; set; }

    /// <summary>The profile that won (researched ?? existing) — the one folded onto the result.</summary>
    public Data.Brand? Saved { get; set; }

    /// <summary>True when the saved profile was fresh, so the network research step is skipped.</summary>
    public bool ResolvedFromCache { get; set; }
}
