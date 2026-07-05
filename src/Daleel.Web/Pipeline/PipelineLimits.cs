namespace Daleel.Web.Pipeline;

/// <summary>
/// The pipeline's fan-out limits. Entity fan-outs are UNCAPPED by default: every discovered brand,
/// store and model is dispatched — dropping work on the floor was a VPS-era self-protection that the
/// Cloudflare execution layer (docs/architecture/cloudflare-workers-pipeline.md) makes obsolete. The
/// real backstops are the ones designed for the job: the per-job cost cap (<c>cost.max_per_job</c>),
/// the workflow deadline + salvage, and the queue retry budgets — all of which keep partial results
/// instead of silently truncating them.
/// </summary>
/// <remarks>
/// Environment knobs (rendered into .env by the deploy workflows) exist as optional RESTRAINTS for an
/// environment that needs them, not as things to raise: <c>PIPELINE_SUBWORKFLOW_CONCURRENCY</c>,
/// <c>PIPELINE_MAX_BRANDS</c>, <c>PIPELINE_MAX_STORES</c>, <c>PIPELINE_MAX_ITEMS</c>. Values are read
/// once at startup (static init) — process-wide tuning, not per-request settings.
/// </remarks>
public static class PipelineLimits
{
    /// <summary>
    /// How many child sub-workflows run at once per dispatch step. This is a throughput WIDTH, not a
    /// result cap — nothing is dropped, work just queues for a slot. Bounds the VPS's own DB/HTTP
    /// pressure (every child opens a scope + connections on this box even when the heavy call runs
    /// on the edge).
    /// </summary>
    public static int SubWorkflowConcurrency { get; } =
        FromEnv("PIPELINE_SUBWORKFLOW_CONCURRENCY", fallback: 16);

    /// <summary>Brands researched per search — every discovered brand, unless an env restraint is set.</summary>
    public static int MaxBrands { get; } = FromEnv("PIPELINE_MAX_BRANDS", fallback: int.MaxValue);

    /// <summary>Stores researched per search — every discovered store, unless an env restraint is set.</summary>
    public static int MaxStores { get; } = FromEnv("PIPELINE_MAX_STORES", fallback: int.MaxValue);

    /// <summary>Models deep-dived per search — every discovered model, unless an env restraint is set.</summary>
    public static int MaxItems { get; } = FromEnv("PIPELINE_MAX_ITEMS", fallback: int.MaxValue);

    /// <summary>
    /// Search jobs processed concurrently by one app instance — UNCAPPED by default (no job waits
    /// because of a count; each job is individually bounded by its own deadline + cost cap).
    /// Restrainable via <c>PIPELINE_MAX_CONCURRENT_JOBS</c>.
    /// </summary>
    public static int ConcurrentJobs { get; } = FromEnv("PIPELINE_MAX_CONCURRENT_JOBS", fallback: int.MaxValue);

    private static int FromEnv(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
    }
}
