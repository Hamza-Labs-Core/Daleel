namespace Daleel.Web.Profiles;

/// <summary>Tunables for the brand/store profile services: how long a profile stays fresh and the clock.</summary>
public sealed class ProfileOptions
{
    /// <summary>A profile older than this is considered stale and gets re-researched. Default 30 days.</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Injectable clock — overridden in tests for deterministic staleness.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Cap on how many stale profiles a single background refresh pass will research.</summary>
    public int MaxRefreshBatch { get; init; } = 20;
}
