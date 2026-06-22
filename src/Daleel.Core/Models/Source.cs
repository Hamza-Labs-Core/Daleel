namespace Daleel.Core.Models;

/// <summary>
/// The kind of Facebook surface a <see cref="Source"/> points at. This drives which
/// Apify actor and input builder the fetcher selects.
/// </summary>
public enum SourceKind
{
    /// <summary>Public keyword search across Facebook posts.</summary>
    Search,

    /// <summary>A specific Facebook group.</summary>
    Group,

    /// <summary>A specific Facebook page.</summary>
    Page
}

/// <summary>
/// A single place to fetch posts from: a search query, a group, or a page.
/// </summary>
public record Source
{
    /// <summary>Human-friendly name for logs and output.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>What kind of surface this source represents.</summary>
    public SourceKind Kind { get; init; } = SourceKind.Search;

    /// <summary>
    /// The target value: a search query for <see cref="SourceKind.Search"/>, or a
    /// group/page URL or id for the other kinds.
    /// </summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>The Apify actor id to run for this source (e.g. "apify/facebook-groups-scraper").</summary>
    public string? ActorId { get; init; }

    /// <summary>Maximum number of items to fetch from this source per run.</summary>
    public int MaxItems { get; init; } = 25;
}
