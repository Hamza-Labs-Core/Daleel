namespace Daleel.Core.Models;

/// <summary>
/// A complete monitoring definition: which keywords to watch for, across which
/// sources, using which match mode. This is what the CLI's <c>monitor</c> command
/// deserializes from a config file and hands to the pipeline.
/// </summary>
public record MonitoringJob
{
    /// <summary>Optional name for the job, used in output and logs.</summary>
    public string Name { get; init; } = "daleel-job";

    /// <summary>The Arabic (or other) keywords to match. Any match counts as a hit.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

    /// <summary>The sources to fetch posts from.</summary>
    public IReadOnlyList<Source> Sources { get; init; } = Array.Empty<Source>();

    /// <summary>How keywords are compared against post text.</summary>
    public MatchMode Mode { get; init; } = MatchMode.Contains;

    /// <summary>
    /// Maximum normalized Levenshtein distance allowed for a <see cref="MatchMode.Fuzzy"/>
    /// hit, as a fraction of the keyword length (0.0–1.0). Ignored for other modes.
    /// </summary>
    public double FuzzyThreshold { get; init; } = 0.25;

    /// <summary>Path the pipeline writes matched posts to, as JSONL.</summary>
    public string OutputPath { get; init; } = "daleel-results.jsonl";
}
