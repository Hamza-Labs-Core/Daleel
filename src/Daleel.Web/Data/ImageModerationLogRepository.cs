using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>Persists and queries the admin-only <see cref="ImageModerationLog"/> — the per-image
/// audit of what the halal vision screen did to every product/brand photo.</summary>
public interface IImageModerationLogRepository
{
    /// <summary>
    /// Records the screen's verdict for a set of images from one job, UPSERTING by (SearchJobId,
    /// ImageUrl): a re-screen (after an infra outage clears) overwrites the previous verdict rather
    /// than adding a duplicate row, so the log always shows the latest decision per image.
    /// </summary>
    Task RecordAsync(IReadOnlyCollection<ImageModerationLog> rows, CancellationToken ct = default);

    /// <summary>The most recent verdicts, newest first, optionally filtered by decision and/or query text.</summary>
    Task<IReadOnlyList<ImageModerationLog>> ListRecentAsync(
        int take, string? decision = null, string? queryLike = null, CancellationToken ct = default);

    /// <summary>Total verdicts recorded since <paramref name="since"/>, split by decision.</summary>
    Task<ImageModerationCounts> CountsSinceAsync(DateTimeOffset since, CancellationToken ct = default);
}

/// <summary>Shown/hidden/unscreened tallies for the admin summary line.</summary>
public sealed record ImageModerationCounts(int Total, int Shown, int Hidden, int Unscreened);

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

        // Upsert by (SearchJobId, ImageUrl). Rows from one screen all share a job, so one indexed
        // query loads any prior verdicts for these exact images; matches are updated in place, the
        // rest inserted. Keeps the log to one row per (job, image) across re-screens.
        var jobIds = rows.Select(r => r.SearchJobId).Distinct().ToList();
        var urls = rows.Select(r => r.ImageUrl).Distinct().ToList();
        var existing = await _db.ImageModerationLogs
            .Where(x => jobIds.Contains(x.SearchJobId) && urls.Contains(x.ImageUrl))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byKey = existing.ToDictionary(x => (x.SearchJobId, x.ImageUrl));
        foreach (var row in rows)
        {
            if (byKey.TryGetValue((row.SearchJobId, row.ImageUrl), out var prior))
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
                prior.CreatedAt = row.CreatedAt;
            }
            else
            {
                _db.ImageModerationLogs.Add(row);
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ImageModerationLog>> ListRecentAsync(
        int take, string? decision = null, string? queryLike = null, CancellationToken ct = default)
    {
        var q = _db.ImageModerationLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(decision))
        {
            q = q.Where(x => x.Decision == decision);
        }

        if (!string.IsNullOrWhiteSpace(queryLike))
        {
            var like = $"%{queryLike.Trim()}%";
            q = q.Where(x => EF.Functions.Like(x.Query!, like) || EF.Functions.Like(x.ItemName!, like));
        }

        return await q.OrderByDescending(x => x.CreatedAt)
            .Take(Math.Clamp(take, 1, 2000))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<ImageModerationCounts> CountsSinceAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var rows = await _db.ImageModerationLogs.AsNoTracking()
            .Where(x => x.CreatedAt >= since)
            .GroupBy(x => x.Decision)
            .Select(g => new { Decision = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        int By(string d) => rows.FirstOrDefault(r => r.Decision == d)?.Count ?? 0;
        var shown = By(ImageModerationDecision.Shown);
        var hidden = By(ImageModerationDecision.Hidden);
        var unscreened = By(ImageModerationDecision.Unscreened);
        return new ImageModerationCounts(shown + hidden + unscreened, shown, hidden, unscreened);
    }
}
