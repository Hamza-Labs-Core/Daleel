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

    /// <summary>Batch rate: applies one verdict to every finding in <paramref name="ids"/>. Returns rows updated.</summary>
    Task<int> RateManyAsync(IReadOnlyCollection<long> ids, int? rating, CancellationToken ct = default);

    /// <summary>
    /// Batch un-filter: whitelists every finding in <paramref name="ids"/> (same semantics as
    /// <see cref="WhitelistAsync"/> per row). Returns how many rows now carry a whitelist entry;
    /// rows with no stable key — or deleted concurrently — are skipped, never faulting the batch.
    /// </summary>
    Task<int> WhitelistManyAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);

    /// <summary>Batch undo of <see cref="WhitelistManyAsync"/>. Returns how many entries were removed.</summary>
    Task<int> RemoveWhitelistManyAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);

    /// <summary>
    /// Batch delete: removes the findings AND any whitelist entries linked to them — a deleted
    /// finding must not leave content silently whitelisted with no UI path to manage it.
    /// Returns rows deleted.
    /// </summary>
    Task<int> DeleteManyAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);

    /// <summary>
    /// Count of legacy findings from the pre-overhaul filter: no content hash and no URLs, so
    /// they can't be whitelisted and (being keyword decisions) never tune thresholds.
    /// </summary>
    Task<int> CountKeylessAsync(CancellationToken ct = default);

    /// <summary>Deletes every legacy keyless finding (see <see cref="CountKeylessAsync"/>). Returns rows deleted.</summary>
    Task<int> PurgeKeylessAsync(CancellationToken ct = default);

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

    public async Task<int> RateManyAsync(IReadOnlyCollection<long> ids, int? rating, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        // One tracked load + one SaveChanges: batches are bounded by the admin page size (200),
        // and the tracked path keeps the Unix-ms RatedAt conversion in one place.
        var rows = await _db.FilteredContentLogs.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            row.Rating = rating is null ? null : Math.Sign(rating.Value);
            row.RatedAt = rating is null ? null : now;
        }

        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task<int> WhitelistManyAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        // Per-row on purpose: WhitelistAsync owns the transaction + unique-index race recovery,
        // and a row that can't be whitelisted (no stable key, deleted meanwhile) must not fault
        // the rest of the batch. Bounded by the admin page size, so N round-trips is fine.
        var done = 0;
        foreach (var id in ids)
        {
            try
            {
                if (await WhitelistAsync(id, ct) is not null)
                {
                    done++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                // row deleted concurrently — skip
            }
        }

        return done;
    }

    public async Task<int> RemoveWhitelistManyAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        var done = 0;
        foreach (var id in ids)
        {
            var row = await _db.FilteredContentLogs.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.WhitelistEntryId })
                .FirstOrDefaultAsync(ct);
            if (row?.WhitelistEntryId is null)
            {
                continue;
            }

            await RemoveWhitelistAsync(id, ct);
            done++;
        }

        return done;
    }

    public async Task<int> DeleteManyAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        var rows = await _db.FilteredContentLogs.Where(x => ids.Contains(x.Id)).ToListAsync(ct);

        // Take the linked whitelist entries with the findings: an entry whose finding is gone
        // would keep content whitelisted forever with no admin surface to see or undo it.
        var entryIds = rows.Where(r => r.WhitelistEntryId is not null)
            .Select(r => r.WhitelistEntryId!.Value)
            .ToList();
        if (entryIds.Count > 0)
        {
            var entries = await _db.ModerationWhitelist.Where(e => entryIds.Contains(e.Id)).ToListAsync(ct);
            _db.ModerationWhitelist.RemoveRange(entries);
        }

        _db.FilteredContentLogs.RemoveRange(rows);
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <summary>Legacy pre-overhaul rows: no stable key of any kind (the new pipeline always writes a hash).</summary>
    private static IQueryable<FilteredContentLog> Keyless(DaleelDbContext db) =>
        db.FilteredContentLogs.Where(x =>
            x.ContentHash == null && x.SourceUrl == null && x.ImageUrl == null && x.WhitelistEntryId == null);

    public async Task<int> CountKeylessAsync(CancellationToken ct = default) =>
        await Keyless(_db).AsNoTracking().CountAsync(ct);

    public async Task<int> PurgeKeylessAsync(CancellationToken ct = default) =>
        // Set-based delete: keyless rows can't carry a whitelist entry (whitelisting needs a key,
        // and the predicate excludes linked rows defensively), so no entry cleanup is needed.
        await Keyless(_db).ExecuteDeleteAsync(ct);

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
