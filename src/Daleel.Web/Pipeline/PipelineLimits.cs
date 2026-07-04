namespace Daleel.Web.Pipeline;

/// <summary>
/// The pipeline's fan-out caps, centralized and environment-tunable. The historical constants
/// (5 concurrent, ≤15/10/20 entities) existed to protect the single VPS that used to execute every
/// scrape/LLM call in-process; with the Cloudflare execution layer the heavy calls run on the edge
/// (docs/architecture/cloudflare-workers-pipeline.md §8.2) and these can be raised per environment
/// without a code change. Defaults preserve today's behavior exactly.
/// </summary>
/// <remarks>
/// Set via environment (rendered into .env by the deploy workflows):
/// <c>PIPELINE_SUBWORKFLOW_CONCURRENCY</c>, <c>PIPELINE_MAX_BRANDS</c>, <c>PIPELINE_MAX_STORES</c>,
/// <c>PIPELINE_MAX_ITEMS</c>. Values are read once at startup (static init) — they are process-wide
/// tuning knobs, not per-request settings.
/// </remarks>
public static class PipelineLimits
{
    /// <summary>How many child sub-workflows run at once per dispatch step.</summary>
    public static int SubWorkflowConcurrency { get; } =
        FromEnv("PIPELINE_SUBWORKFLOW_CONCURRENCY", fallback: 5, min: 1, max: 64);

    /// <summary>Brands researched per search (one BrandResearchWorkflow each).</summary>
    public static int MaxBrands { get; } = FromEnv("PIPELINE_MAX_BRANDS", fallback: 15, min: 1, max: 200);

    /// <summary>Stores researched per search (one StoreResearchWorkflow each).</summary>
    public static int MaxStores { get; } = FromEnv("PIPELINE_MAX_STORES", fallback: 10, min: 1, max: 200);

    /// <summary>Models deep-dived per search (one ItemDeepDiveWorkflow each).</summary>
    public static int MaxItems { get; } = FromEnv("PIPELINE_MAX_ITEMS", fallback: 20, min: 1, max: 200);

    private static int FromEnv(string name, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var parsed) ? Math.Clamp(parsed, min, max) : fallback;
    }
}
