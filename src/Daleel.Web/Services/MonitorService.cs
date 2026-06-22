using Daleel.Apify;
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
    public required string Keyword { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? Author { get; init; }
    public string? Source { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset MatchedAt { get; init; } = DateTimeOffset.UtcNow;
    public Sentiment Sentiment { get; init; } = Sentiment.Neutral;
}

/// <summary>
/// In-memory store of monitoring jobs and the matched-post feed, shared across all circuits
/// (registered as a singleton). A "run now" pass uses the real Apify fetcher + Arabic matcher
/// when a token is available, and otherwise reports that social monitoring is unconfigured.
/// </summary>
public sealed class MonitorService
{
    private readonly IAgentFactory _factory;
    private readonly object _gate = new();
    private readonly List<MonitorDefinition> _monitors = new();
    private readonly List<MonitorHit> _feed = new();

    public MonitorService(IAgentFactory factory) => _factory = factory;

    /// <summary>Snapshot of all monitors, newest first.</summary>
    public IReadOnlyList<MonitorDefinition> Monitors
    {
        get { lock (_gate) { return _monitors.OrderByDescending(m => m.CreatedAt).ToList(); } }
    }

    /// <summary>Snapshot of the results feed, newest first (capped).</summary>
    public IReadOnlyList<MonitorHit> Feed
    {
        get { lock (_gate) { return _feed.OrderByDescending(h => h.MatchedAt).Take(200).ToList(); } }
    }

    public MonitorDefinition Add(string keyword, string geo, int intervalMinutes)
    {
        var monitor = new MonitorDefinition { Keyword = keyword.Trim(), Geo = geo, IntervalMinutes = intervalMinutes };
        lock (_gate)
        {
            _monitors.Add(monitor);
        }

        return monitor;
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            _monitors.RemoveAll(m => m.Id == id);
            _feed.RemoveAll(h => h.MonitorId == id);
        }
    }

    public void Toggle(string id)
    {
        lock (_gate)
        {
            var m = _monitors.FirstOrDefault(x => x.Id == id);
            if (m is not null)
            {
                m.Status = m.Status == MonitorStatus.Active ? MonitorStatus.Paused : MonitorStatus.Active;
            }
        }
    }

    /// <summary>
    /// Runs one monitoring pass: fetches recent posts for the keyword from the market's primary
    /// Apify actor, keeps Arabic-aware matches, scores a coarse sentiment, and appends to the feed.
    /// Returns the number of new hits, or -1 when no Apify token is configured.
    /// </summary>
    public async Task<int> RunOnceAsync(string id, IReadOnlyDictionary<string, string>? keys, CancellationToken ct = default)
    {
        MonitorDefinition? monitor;
        lock (_gate)
        {
            monitor = _monitors.FirstOrDefault(m => m.Id == id);
        }

        if (monitor is null)
        {
            return 0;
        }

        var token = _factory.Resolve("APIFY_TOKEN", keys);
        if (token is null)
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

        using var client = new ApifyClient(token);
        var fetcher = new ApifyPostFetcher(client);
        var matcher = new ArabicMatcher();
        var keywords = new[] { monitor.Keyword };

        var posts = await fetcher.FetchAsync(source, monitor.Keyword, ct).ConfigureAwait(false);
        var hits = posts
            .Where(p => matcher.Match(p.Text, keywords, MatchMode.Contains).IsMatch)
            .Select(p => new MonitorHit
            {
                MonitorId = monitor.Id,
                Keyword = monitor.Keyword,
                Text = p.Text,
                Author = p.Author,
                Source = p.Source ?? profile.Key,
                Url = p.Url,
                MatchedAt = p.Timestamp ?? DateTimeOffset.UtcNow,
                Sentiment = ScoreSentiment(p.Text)
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

    /// <summary>Coarse keyword sentiment (Arabic + English), mirroring the agent's fallback scorer.</summary>
    private static Sentiment ScoreSentiment(string text)
    {
        var n = ArabicNormalizer.Normalize(text);
        var positive = new[] { "ممتاز", "رائع", "جيد", "افضل", "احسن", "good", "great", "best", "excellent", "love" };
        var negative = new[] { "سيء", "رديء", "مشكله", "خربان", "bad", "worst", "broken", "problem", "hate" };
        var pos = positive.Count(w => n.Contains(ArabicNormalizer.Normalize(w), StringComparison.Ordinal));
        var neg = negative.Count(w => n.Contains(ArabicNormalizer.Normalize(w), StringComparison.Ordinal));
        return pos > neg ? Sentiment.Positive : neg > pos ? Sentiment.Negative : Sentiment.Neutral;
    }
}
