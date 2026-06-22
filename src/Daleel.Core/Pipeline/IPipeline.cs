using Daleel.Core.Models;

namespace Daleel.Core.Pipeline;

/// <summary>
/// Aggregate counters describing what a single pipeline run did.
/// </summary>
public record PipelineReport
{
    public int SourcesProcessed { get; init; }
    public int PostsFetched { get; init; }
    public int Duplicates { get; init; }
    public int Matches { get; init; }
}

/// <summary>
/// Orchestrates the end-to-end flow: fetch → normalize → match → dedup → write.
/// </summary>
public interface IPipeline
{
    /// <summary>Runs <paramref name="job"/> to completion and returns a summary report.</summary>
    Task<PipelineReport> RunAsync(MonitoringJob job, CancellationToken cancellationToken = default);
}
