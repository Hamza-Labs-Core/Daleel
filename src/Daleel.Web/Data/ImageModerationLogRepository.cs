using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>Persists and queries the <see cref="ImageModerationLog"/> registry — one row per distinct
/// product/brand image URL with its latest halal-screen verdict — and drives the re-evaluation queue.</summary>
public interface IImageModerationLogRepository
{
    /// <summary>
    /// Records the screen's verdict for a set of images, UPSERTING by ImageUrl: re-seeing / re-screening
    /// an image overwrites its single registry row rather than adding a duplicate, so the registry always
    /// shows the latest decision per distinct image. A pending re-eval flag is left untouched (only the
    /// re-eval processor clears it).
    /// </summary>
    Task RecordAsync(IReadOnlyCollection<ImageModerationLog> rows, CancellationToken ct = default);

    /// <summary>The registry rows, newest-screened first, optionally filtered by decision and/or text.</summary>
    Task<IReadOnlyList<ImageModerationLog>> ListRecentAsync(
        int take, string? decision = null, string? queryLike = null, CancellationToken ct = default);

    /// <summary>Whole-registry tallies for the admin summary line.</summary>
    Task<ImageModerationCounts> RegistryCountsAsync(CancellationToken ct = default);

    /// <summary>Flags the given registry rows for re-evaluation (queues them). Returns rows flagged.</summary>
    Task<int> FlagForReEvalAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);

    /// <summary>Flags EVERY row matching the filter for re-evaluation (the "re-evaluate all" action).</summary>
    Task<int> FlagAllForReEvalAsync(string? decision, string? queryLike, CancellationToken ct = default);

    /// <summary>The oldest-flagged rows awaiting re-evaluation (the queue head), up to <paramref name="take"/>.</summary>
    Task<IReadOnlyList<ImageModerationLog>> ClaimReEvalBatchAsync(int take, CancellationToken ct = default);

    /// <summary>Writes a re-evaluation's fresh verdict and clears the queue marker for that image.</summary>
    Task ApplyReEvalVerdictAsync(
        long id, string decision, string? category, double? score, string? reason, string? source,
        DateTimeOffset screenedAt, CancellationToken ct = default);
}

/// <summary>Registry tallies for the admin summary line (Pending = queued for re-evaluation).</summary>
public sealed record ImageModerationCounts(int Total, int Shown, int Hidden, int Unscreened, int Pending);

public sealed class ImageModerationLogRepository : IImageModerationLogRepository
{
    private readonly DaleelDbContext _db;

    public ImageModerationLogRepository(DaleelDbContext db) => _db = db;

    public async Task RecordAsync(IReadOnlyCollection<ImageModerationLog> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0)
        {
            return;
        }

        // One registry row per distinct URL: dedup the incoming batch by URL (last wins), then upsert.
        var byUrl = new Dictionary<string, ImageModerationLog>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            byUrl[row.ImageUrl] = row;
        }

        var urls = byUrl.Keys.ToList();
        var existing = await _db.ImageModerationLogs
            .Where(x => urls.Contains(x.ImageUrl))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var existingByUrl = existing.ToDictionary(x => x.ImageUrl, StringComparer.Ordinal);

        foreach (var (url, row) in byUrl)
        {
            if (existingByUrl.TryGetValue(url, out var prior))
            {
                prior.Decision = row.Decision;
                prior.Category = row.Category;
                prior.Score = row.Score;
                prior.Reason = row.Reason;
                prior.DecisionSource = row.DecisionSource;
                prior.ItemName = row.ItemName;
                prior.ItemKind = row.ItemKind;
                prior.Query = row.Query;
                prior.Geo = row.Geo;
                prior.SearchJobId = row.SearchJobId;
                prior.CreatedAt = row.CreatedAt;
                // ReEvalRequestedAt intentionally preserved — only the processor clears it.
            }
            else
            {
                _db.ImageModerationLogs.Add(row);
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ImageModerationLog>> ListRecentAsync(
        int take, string? decision = null, string? queryLike = null, CancellationToken ct = default) =>
        await FilteredQuery(decision, queryLike).AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(Math.Clamp(take, 1, 2000))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<ImageModerationCounts> RegistryCountsAsync(CancellationToken ct = default)
    {
        var rows = await _db.ImageModerationLogs.AsNoTracking()
            .GroupBy(x => x.Decision)
            .Select(g => new { Decision = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var pending = await _db.ImageModerationLogs.AsNoTracking()
            .CountAsync(x => x.ReEvalRequestedAt != null, ct)
            .ConfigureAwait(false);

        int By(string d) => rows.FirstOrDefault(r => r.Decision == d)?.Count ?? 0;
        var shown = By(ImageModerationDecision.Shown);
        var hidden = By(ImageModerationDecision.Hidden);
        var unscreened = By(ImageModerationDecision.Unscreened);
        return new ImageModerationCounts(shown + hidden + unscreened, shown, hidden, unscreened, pending);
    }

    public async Task<int> FlagForReEvalAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        return await _db.ImageModerationLogs
            .Where(x => ids.Contains(x.Id) && x.ReEvalRequestedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReEvalRequestedAt, now), ct)
            .ConfigureAwait(false);
    }

    public async Task<int> FlagAllForReEvalAsync(string? decision, string? queryLike, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await FilteredQuery(decision, queryLike)
            .Where(x => x.ReEvalRequestedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReEvalRequestedAt, now), ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ImageModerationLog>> ClaimReEvalBatchAsync(int take, CancellationToken ct = default) =>
        await _db.ImageModerationLogs.AsNoTracking()
            .Where(x => x.ReEvalRequestedAt != null)
            .OrderBy(x => x.ReEvalRequestedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task ApplyReEvalVerdictAsync(
        long id, string decision, string? category, double? score, string? reason, string? source,
        DateTimeOffset screenedAt, CancellationToken ct = default) =>
        await _db.ImageModerationLogs
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Decision, decision)
                .SetProperty(x => x.Category, category)
                .SetProperty(x => x.Score, score)
                .SetProperty(x => x.Reason, reason)
                .SetProperty(x => x.DecisionSource, source)
                .SetProperty(x => x.CreatedAt, screenedAt)
                .SetProperty(x => x.ReEvalRequestedAt, (DateTimeOffset?)null), ct)
            .ConfigureAwait(false);

    private IQueryable<ImageModerationLog> FilteredQuery(string? decision, string? queryLike)
    {
        var q = _db.ImageModerationLogs.AsQueryable();

        if (!string.IsNullOrWhiteSpace(decision))
        {
            q = q.Where(x => x.Decision == decision);
        }

        if (!string.IsNullOrWhiteSpace(queryLike))
        {
            var like = $"%{queryLike.Trim()}%";
            q = q.Where(x => EF.Functions.Like(x.Query!, like) || EF.Functions.Like(x.ItemName!, like));
        }

        return q;
    }
}
