namespace Daleel.Core.Llm;

/// <summary>
/// One model-configurable LLM call-site in the search pipeline. Each call-site resolves its model
/// independently from config (<see cref="ConfigKey"/> = <c>model.&lt;Key&gt;</c>) so an operator can
/// cost-tune each step, and every metered call is stamped with the call-site so analytics can show
/// which steps fire — and cost — most.
/// </summary>
public sealed record LlmCallSite(string Key, string DisplayName, string DefaultModel)
{
    /// <summary>The <c>SystemConfig</c> key that overrides this call-site's model, e.g. <c>model.extraction</c>.</summary>
    public string ConfigKey => "model." + Key;
}

/// <summary>
/// The registry of pipeline LLM call-sites. Keeping them here (Core) lets the config seeder and the
/// admin model editor enumerate the same canonical list the pipeline routes on.
/// </summary>
public static class LlmCallSites
{
    /// <summary>A capable default for every step — the model the enrichment actors already standardised on.
    /// Per-site config overrides let an operator downgrade cheap steps or upgrade hard ones without a redeploy.</summary>
    public const string DefaultModel = "anthropic/claude-sonnet-5";

    public static readonly LlmCallSite Planner = new("planner", "Query planner", DefaultModel);
    public static readonly LlmCallSite Category = new("category", "Category analysis", DefaultModel);
    public static readonly LlmCallSite Extraction = new("extraction", "Product extraction", DefaultModel);
    public static readonly LlmCallSite Relevance = new("relevance", "Relevance gate", DefaultModel);
    public static readonly LlmCallSite Analyst = new("analyst", "Analyst summary", DefaultModel);
    public static readonly LlmCallSite Synthesis = new("synthesis", "Freeform synthesis", DefaultModel);
    public static readonly LlmCallSite BrandReputation = new("brand_reputation", "Brand reputation", DefaultModel);
    public static readonly LlmCallSite EnrichModel = new("enrich_model", "Model enrichment", DefaultModel);

    /// <summary>The LLM-driven site crawler's navigation + deep-dive decisions (assess / paginate / deep-dive).</summary>
    public static readonly LlmCallSite Crawl = new("crawl", "Site crawl navigation", DefaultModel);

    /// <summary>Every registered call-site — drives config seeding and the admin model editor.</summary>
    public static readonly IReadOnlyList<LlmCallSite> All = new[]
    {
        Planner, Category, Extraction, Relevance, Analyst, Synthesis, BrandReputation, EnrichModel, Crawl,
    };

    private static readonly IReadOnlyDictionary<string, LlmCallSite> ByKey =
        All.ToDictionary(s => s.Key, StringComparer.Ordinal);

    /// <summary>The registry-default model for a call-site key, or the global default for an unknown key.</summary>
    public static string DefaultFor(string? callSiteKey) =>
        callSiteKey is not null && ByKey.TryGetValue(callSiteKey, out var site) ? site.DefaultModel : DefaultModel;
}

/// <summary>
/// Ambient, async-flowing marker for the LLM call-site currently executing. A pipeline step opens a
/// scope around its LLM call — <c>using (LlmCallSiteScope.Enter(LlmCallSites.Extraction))</c> — and
/// <see cref="RoutingLlmClient"/> reads it to select that call-site's configured model while the
/// logging client reads it to stamp the call-site onto the metered call. It is <see cref="AsyncLocal{T}"/>,
/// so parallel calls (e.g. per-page extraction fan-out) each carry their own call-site independently.
/// </summary>
public static class LlmCallSiteScope
{
    private static readonly AsyncLocal<string?> Value = new();

    /// <summary>The call-site key of the innermost active scope, or null when outside any scope.</summary>
    public static string? Current => Value.Value;

    public static IDisposable Enter(LlmCallSite site) => Enter(site.Key);

    public static IDisposable Enter(string callSiteKey)
    {
        var prior = Value.Value;
        Value.Value = callSiteKey;
        return new Popper(prior);
    }

    private sealed class Popper : IDisposable
    {
        private readonly string? _prior;
        private bool _popped;

        public Popper(string? prior) => _prior = prior;

        public void Dispose()
        {
            if (_popped)
            {
                return;
            }

            _popped = true;
            Value.Value = _prior;
        }
    }
}
