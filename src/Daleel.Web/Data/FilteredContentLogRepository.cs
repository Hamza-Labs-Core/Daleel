using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>Per-category rating tallies used to tune classifier thresholds (the feedback loop).</summary>
public sealed record CategoryRatingStats(string Category, int Correct, int Incorrect);

/// <summary>Persists and queries the admin-only <see cref="FilteredContentLog"/> and its feedback.</summary>
public interface IFilteredContentLogRepository
{
    Task AddBatchAsync(IEnumerable<FilteredContentLog> rows, CancellationToken ct = default);
    Task<IReadOnlyList<FilteredContentLog>> ListRecentAsync(int take, CancellationToken ct = default);
    Task<int> CountSinceAsync(DateTimeOffset since, CancellationToken ct = default);

    /// <summary>Records the admin's verdict on a finding: +1 correct, -1 incorrect, null to clear.</summary>
    Task RateAsync(long id, int? rating, CancellationToken ct = default);

    /// <summary>
    /// Un-filters the finding: creates a whitelist entry from its most specific stable key
    /// (image URL for image findings, else content hash, else source URL) and links it to the
    /// row. Returns the entry id, or null when the finding has no usable key or is already
    /// whitelisted. Also rates the finding "incorrect" if unrated — undoing implies the filter
    /// got it wrong, and the feedback loop should learn from it.
    /// </summary>
    Task<long?> WhitelistAsync(long id, CancellationToken ct = default);

    /// <summary>Reverts the "undo": removes the finding's whitelist entry so filtering applies again.</summary>
    Task RemoveWhitelistAsync(long id, CancellationToken ct = default);

    /// <summary>Per-category correct/incorrect tallies for LLM/vision findings rated since <paramref name="since"/>.</summary>
    Task<IReadOnlyList<CategoryRatingStats>> RatingStatsAsync(DateTimeOffset since, CancellationToken ct = default);
}

/// <summary>Loads the active whitelist keys the moderation pipeline consults on every run.</summary>
public interface IModerationWhitelistRepository
{
    Task<IReadOnlyCollection<string>> ActiveKeysAsync(CancellationToken ct = default);
}

public sealed class FilteredContentLogRepository : IFilteredContentLogRepository, IModerationWhitelistRepository
{
    private readonly DaleelDbContext _db;

    public FilteredContentLogRepository(DaleelDbContext db) => _db = db;

    public async Task AddBatchAsync(IEnumerable<FilteredContentLog> rows, CancellationToken ct = default)
    {
        _db.FilteredContentLogs.AddRange(rows);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<FilteredContentLog>> ListRecentAsync(int take, CancellationToken ct = default) =>
        // Order by the auto-increment Id (newest = highest), not CreatedAt: the monotonic identity key
        // gives a stable, provider-agnostic newest-first ordering.
        await _db.FilteredContentLogs.AsNoTracking()
            .OrderByDescending(x => x.Id)
            .Take(take)
            .ToListAsync(ct);

    public async Task<int> CountSinceAsync(DateTimeOffset since, CancellationToken ct = default) =>
        await _db.FilteredContentLogs.AsNoTracking().CountAsync(x => x.CreatedAt >= since, ct);

    public async Task RateAsync(long id, int? rating, CancellationToken ct = default)
    {
        var row = await _db.FilteredContentLogs.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException($"Finding #{id} no longer exists.");

        row.Rating = rating is null ? null : Math.Sign(rating.Value);
        row.RatedAt = rating is null ? null : DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<long?> WhitelistAsync(long id, CancellationToken ct = default)
    {
        var row = await _db.FilteredContentLogs.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException($"Finding #{id} no longer exists.");

        if (row.WhitelistEntryId is not null)
        {
            return row.WhitelistEntryId;
        }

        // A previous attempt may have inserted the entry but died before linking it to the row
        // (or another circuit raced us). The unique index on SourceLogId makes this lookup the
        // single source of truth — relink instead of inserting a duplicate.
        if (await _db.ModerationWhitelist.FirstOrDefaultAsync(x => x.SourceLogId == id, ct) is { } orphan)
        {
            row.WhitelistEntryId = orphan.Id;
            row.Rating ??= -1;
            row.RatedAt ??= DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return orphan.Id;
        }

        // Most specific key first: an image finding un-hides that image; otherwise the content
        // hash un-hides the same item text wherever it reappears; the URL is the last resort.
        var (key, matchType) = row.Field == "image" && row.ImageUrl is not null ? (row.ImageUrl, "image")
            : row.ContentHash is not null ? (row.ContentHash, "hash")
            : row.SourceUrl is not null ? (row.SourceUrl, "url")
            : (null, null);
        if (key is null)
        {
            return null;
        }

        var entry = new ModerationWhitelistEntry
        {
            Key = key,
            MatchType = matchType!,
            Category = row.Category,
            Note = row.Content,
            SourceLogId = row.Id,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.ModerationWhitelist.Add(entry);

        // Undoing a filter decision implies it was wrong — feed that back into the thresholds
        // unless the admin already rated the finding explicitly.
        row.Rating ??= -1;
        row.RatedAt ??= DateTimeOffset.UtcNow;

        // One transaction across the insert and the row link: either both land or neither does,
        // so a live-but-unlinked whitelist entry can't be left silently bypassing moderation.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            await _db.SaveChangesAsync(ct);
            row.WhitelistEntryId = entry.Id;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return entry.Id;
        }
        catch (DbUpdateException)
        {
            // Unique-index race: another circuit inserted the entry between our lookup and commit.
            // Roll back our attempt and link to the winner's entry instead.
            await tx.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();

            var winner = await _db.ModerationWhitelist.FirstOrDefaultAsync(x => x.SourceLogId == id, ct);
            if (winner is null)
            {
                throw; // not the race — surface the real failure
            }

            var freshRow = await _db.FilteredContentLogs.FirstAsync(x => x.Id == id, ct);
            freshRow.WhitelistEntryId = winner.Id;
            freshRow.Rating ??= -1;
            freshRow.RatedAt ??= DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return winner.Id;
        }
    }

    public async Task RemoveWhitelistAsync(long id, CancellationToken ct = default)
    {
        var row = await _db.FilteredContentLogs.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row?.WhitelistEntryId is not { } entryId)
        {
            return;
        }

        var entry = await _db.ModerationWhitelist.FirstOrDefaultAsync(x => x.Id == entryId, ct);
        if (entry is not null)
        {
            _db.ModerationWhitelist.Remove(entry);
        }

        row.WhitelistEntryId = null;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<CategoryRatingStats>> RatingStatsAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        // Only model decisions are threshold-tunable; keyword rules are deterministic (their
        // feedback still matters — surfaced in the admin UI — but doesn't move thresholds).
        var rows = await _db.FilteredContentLogs.AsNoTracking()
            .Where(x => x.Rating != null && x.CreatedAt >= since
                && (x.DecisionSource == "llm" || x.DecisionSource == "vision"))
            .GroupBy(x => x.Category)
            .Select(g => new
            {
                Category = g.Key,
                Correct = g.Count(x => x.Rating > 0),
                Incorrect = g.Count(x => x.Rating < 0)
            })
            .ToListAsync(ct);

        return rows.Select(r => new CategoryRatingStats(r.Category, r.Correct, r.Incorrect)).ToList();
    }

    public async Task<IReadOnlyCollection<string>> ActiveKeysAsync(CancellationToken ct = default) =>
        await _db.ModerationWhitelist.AsNoTracking().Select(x => x.Key).ToListAsync(ct);
}
