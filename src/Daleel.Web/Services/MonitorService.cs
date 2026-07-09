using Daleel.Apify;
using Daleel.Core.Analysis;
using Daleel.Core.Arabic;
using Daleel.Core.Geo;
using Daleel.Core.Models;

namespace Daleel.Web.Services;

/// <summary>Whether a monitor is actively watching or paused.</summary>
public enum MonitorStatus
{
    Active,
    Paused
}

/// <summary>A keyword-monitoring definition managed from the dashboard.</summary>
public sealed class MonitorDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Owner of this monitor — the isolation key. Set by the service, never the client.</summary>
    public required string UserId { get; init; }

    public required string Keyword { get; set; }
    public string Geo { get; set; } = "jordan";
    public int IntervalMinutes { get; set; } = 60;
    public MonitorStatus Status { get; set; } = MonitorStatus.Active;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastRunAt { get; set; }
    public int MatchCount { get; set; }
}

/// <summary>A single matched post in the results feed.</summary>
public sealed record MonitorHit
{
    public required string MonitorId { get; init; }

    /// <summary>Owner of the monitor that produced this hit — the feed's isolation key.</summary>
    public required string UserId { get; init; }

    public required string Keyword { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? Author { get; init; }
    public string? Source { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset MatchedAt { get; init; } = DateTimeOffset.UtcNow;
    public Sentiment Sentiment { get; init; } = Sentiment.Neutral;
}

/// <summary>
/// In-memory store of monitoring jobs and the matched-post feed (registered as a singleton).
/// Although the store is process-wide, every operation is scoped to the caller's <c>userId</c> so a
/// user only ever sees, mutates, or runs their own monitors — the same isolation the persisted
/// repositories enforce. A "run now" pass uses the real Apify fetcher + Arabic matcher when a token
/// is available, and otherwise reports that social monitoring is unconfigured.
/// </summary>
public sealed class MonitorService
{
    private readonly IAgentFactory _factory;
    private readonly IProviderApi _providers;
    private readonly object _gate = new();
    private readonly List<MonitorDefinition> _monitors = new();
    private readonly List<MonitorHit> _feed = new();

    public MonitorService(IAgentFactory factory, IProviderApi? providers = null)
    {
        _factory = factory;
        // Optional so existing test wiring keeps working; production DI always supplies the gateway.
        _providers = providers ?? new ProviderApi(factory);
    }

    /// <summary>Snapshot of one user's monitors, newest first.</summary>
    public IReadOnlyList<MonitorDefinition> MonitorsFor(string userId)
    {
        lock (_gate) { return _monitors.Where(m => m.UserId == userId).OrderByDescending(m => m.CreatedAt).ToList(); }
    }

    /// <summary>Snapshot of one user's results feed, newest first (capped).</summary>
    public IReadOnlyList<MonitorHit> FeedFor(string userId)
    {
        lock (_gate) { return _feed.Where(h => h.UserId == userId).OrderByDescending(h => h.MatchedAt).Take(200).ToList(); }
    }

    public MonitorDefinition Add(string userId, string keyword, string geo, int intervalMinutes)
    {
        var monitor = new MonitorDefinition
        {
            UserId = userId, Keyword = keyword.Trim(), Geo = geo, IntervalMinutes = intervalMinutes
        };
        lock (_gate)
        {
            _monitors.Add(monitor);
        }

        return monitor;
    }

    public void Remove(string userId, string id)
    {
        lock (_gate)
        {
            // The userId predicate is the isolation boundary: a request can only delete its own rows.
            _monitors.RemoveAll(m => m.Id == id && m.UserId == userId);
            _feed.RemoveAll(h => h.MonitorId == id && h.UserId == userId);
        }
    }

    public void Toggle(string userId, string id)
    {
        lock (_gate)
        {
            var m = _monitors.FirstOrDefault(x => x.Id == id && x.UserId == userId);
            if (m is not null)
            {
                m.Status = m.Status == MonitorStatus.Active ? MonitorStatus.Paused : MonitorStatus.Active;
            }
        }
    }

    /// <summary>
    /// Runs one monitoring pass: fetches recent posts for the keyword from the market's primary
    /// Apify actor, keeps Arabic-aware matches, scores a coarse sentiment, and appends to the feed.
    /// Returns the number of new hits, or -1 when no Apify token is configured. The monitor must be
    /// owned by <paramref name="userId"/>, else this is a no-op (returns 0).
    /// </summary>
    public async Task<int> RunOnceAsync(string userId, string id, CancellationToken ct = default)
    {
        MonitorDefinition? monitor;
        lock (_gate)
        {
            monitor = _monitors.FirstOrDefault(m => m.Id == id && m.UserId == userId);
        }

        if (monitor is null)
        {
            return 0;
        }

        if (!_providers.HasSocial)
        {
            return -1;
        }

        var profile = GeoProfiles.ResolveOrDefault(monitor.Geo);
        var actor = profile.ApifyActors.FirstOrDefault() ?? "scrapeforge/facebook-search-posts";
        var source = new Source
        {
            Name = $"{profile.Key}-monitor",
            Kind = SourceKind.Search,
            Target = monitor.Keyword,
            ActorId = actor,
            MaxItems = 25
        };

        // Through the gateway — metered by construction (this was one of the two unmetered direct
        // provider constructions the audit caught; monitor fetches now show in the usage log too).
        var matcher = new ArabicMatcher();
        var keywords = new[] { monitor.Keyword };

        var posts = await _providers.FetchSocialPostsAsync(source, monitor.Keyword, ct).ConfigureAwait(false);
        var hits = posts
            .Where(p => matcher.Match(p.Text, keywords, MatchMode.Contains).IsMatch)
            .Select(p => new MonitorHit
            {
                MonitorId = monitor.Id,
                UserId = monitor.UserId,
                Keyword = monitor.Keyword,
                Text = p.Text,
                Author = p.Author,
                Source = p.Source ?? profile.Key,
                Url = p.Url,
                MatchedAt = p.Timestamp ?? DateTimeOffset.UtcNow,
                Sentiment = KeywordSentiment.Score(p.Text)
            })
            .ToList();

        lock (_gate)
        {
            _feed.AddRange(hits);
            monitor.LastRunAt = DateTimeOffset.UtcNow;
            monitor.MatchCount += hits.Count;
        }

        return hits.Count;
    }
}
