using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>
/// Persistence for the DeepL translation cache. Reads are batch composite-key lookups (many source hashes
/// for one target language); writes are best-effort inserts that tolerate a concurrent writer having
/// already cached the same pair. Registered <b>Transient</b> so each consumer gets its own
/// <see cref="DaleelDbContext"/> — overlapping translations from independent UI components must never share
/// one Npgsql connection (the Blazor-circuit concurrency hazard the profile repositories avoid the same way).
/// </summary>
public interface ITranslationRepository
{
    /// <summary>
    /// Returns the cached translations for the given source hashes into <paramref name="targetLang"/>, keyed
    /// by source hash. Only rows created at or after <paramref name="notOlderThan"/> are returned — older
    /// rows are treated as misses so the configurable max-age sweep forces a re-fetch.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetFreshAsync(
        IReadOnlyCollection<string> sourceHashes, string targetLang, DateTimeOffset notOlderThan,
        CancellationToken ct = default);

    /// <summary>Caches new translations. Best-effort: rows whose key a concurrent writer already inserted are skipped.</summary>
    Task SaveAsync(IReadOnlyCollection<TranslationCacheEntry> entries, CancellationToken ct = default);
}

public sealed class TranslationRepository : ITranslationRepository
{
    private readonly DaleelDbContext _db;

    public TranslationRepository(DaleelDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<string, string>> GetFreshAsync(
        IReadOnlyCollection<string> sourceHashes, string targetLang, DateTimeOffset notOlderThan,
        CancellationToken ct = default)
    {
        if (sourceHashes.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var hashes = sourceHashes as ICollection<string> ?? sourceHashes.ToList();
        var rows = await _db.TranslationCache.AsNoTracking()
            .Where(x => x.TargetLang == targetLang
                        && x.CreatedAt >= notOlderThan
                        && hashes.Contains(x.SourceHash))
            .Select(x => new { x.SourceHash, x.TranslatedText })
            .ToListAsync(ct);

        // A row per (hash, lang) is guaranteed unique by the index, so the last-wins dedupe is just defensive.
        var map = new Dictionary<string, string>(rows.Count);
        foreach (var r in rows)
        {
            map[r.SourceHash] = r.TranslatedText;
        }
        return map;
    }

    public async Task SaveAsync(IReadOnlyCollection<TranslationCacheEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0)
        {
            return;
        }

        _db.TranslationCache.AddRange(entries);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent writer already cached one of these (hash, lang) pairs and won the unique-index
            // race. Caching is best-effort — the other writer's row is just as good — so drop the whole batch
            // rather than retry row-by-row. Clear the tracker so this failed SaveChanges can't poison a later
            // write on this context.
            _db.ChangeTracker.Clear();
        }
    }
}
