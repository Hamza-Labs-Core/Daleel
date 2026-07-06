using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>
/// Persistence for <see cref="WorkContext"/> rows — the findings ledger and the LLM synthesis, one
/// row per <c>(SearchJobId, Scope, Key)</c>. Like the enrichment queue it is a singleton that opens
/// its own DbContext scope per call (callers may be singletons / background services). Writes upsert
/// on the unique index and are best-effort-idempotent: a concurrent first-insert races to a
/// <see cref="DbUpdateException"/>, which is caught, the row reloaded, and the operation retried once.
/// </summary>
public interface IWorkContextStore
{
    /// <summary>
    /// Appends one advisory finding to <c>(jobId, scope, key)</c>, upserting the row. Bounds the
    /// ledger to the last <c>MaxFindings</c> entries so it can't grow unbounded. Best-effort — a lost
    /// append only weakens the reducer's narrative, never corrupts data.
    /// </summary>
    Task AppendFindingAsync(
        int jobId, string scope, string key, string step, string note, CancellationToken ct = default);

    /// <summary>Loads the row (or null). The synthesis handler reads the ledger + the folded-count HWM here.</summary>
    Task<WorkContext?> GetAsync(int jobId, string scope, string key, CancellationToken ct = default);

    /// <summary>All rows for a job (page render / admin / the reducer's per-scope grouping).</summary>
    Task<IReadOnlyList<WorkContext>> ListForJobAsync(int jobId, CancellationToken ct = default);

    /// <summary>
    /// Upserts the synthesis for <c>(jobId, scope, key)</c>: sets <see cref="WorkContext.Synthesis"/>,
    /// records <paramref name="foldedCount"/> as the high-water mark, bumps the version, stamps the
    /// time. Keyed on the unique index — a re-run updates the same row, never a duplicate.
    /// </summary>
    Task SetSynthesisAsync(
        int jobId, string scope, string key, string synthesis, int foldedCount, CancellationToken ct = default);

    /// <summary>
    /// Records the high-water mark for an entity the reducer CONSIDERED but produced no summary for
    /// (the LLM omitted it, or it was past the batch head cap this round) — leaving
    /// <see cref="WorkContext.Synthesis"/> untouched. This is what stops a retry from re-billing an
    /// unchanged entity: without it, an entity with no synthesis stays eligible forever.
    /// </summary>
    Task MarkSynthesizedAsync(
        int jobId, string scope, string key, int foldedCount, CancellationToken ct = default);

    /// <summary>Deletes rows created before <paramref name="olderThan"/> (TTL sweep). Returns the count removed.</summary>
    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}

public sealed class WorkContextStore : IWorkContextStore
{
    /// <summary>Ledger cap — the reducer only ever reads the last few, and the column is length-bounded.</summary>
    private const int MaxFindings = 40;

    /// <summary>Per-note cap: a single finding is a short advisory line, not a document.</summary>
    private const int MaxNoteChars = 240;

    /// <summary>Per-step-label cap — a short producer tag, not free text.</summary>
    private const int MaxStepChars = 40;

    /// <summary>Column limits (see the entity config). Keys/scopes are capped defensively so an over-long
    /// value (a pathological LLM-extracted brand name) can never fault a write on the varchar bound.</summary>
    private const int MaxKeyChars = 200;
    private const int MaxScopeChars = 16;

    /// <summary>Serialized FindingsJson budget, kept safely under the 8000-char column even at the cap.</summary>
    private const int MaxFindingsJsonChars = 7000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IServiceScopeFactory _scopeFactory;

    public WorkContextStore(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    private sealed record Finding(string Step, string Note, long At);

    public async Task AppendFindingAsync(
        int jobId, string scope, string key, string step, string note, CancellationToken ct = default)
    {
        var finding = new Finding(Clip(step, MaxStepChars), Clip(note, MaxNoteChars),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        await UpsertAsync(jobId, scope, key, row =>
        {
            var findings = Deserialize(row.FindingsJson);
            findings.Add(finding);
            // Keep only the most recent — a bounded, append-only tail.
            if (findings.Count > MaxFindings)
            {
                findings.RemoveRange(0, findings.Count - MaxFindings);
            }

            // Also bound the SERIALIZED size under the varchar(8000) column — drop oldest until it fits,
            // so no caller (however long its notes) can ever overflow the column and fault the write.
            var json = JsonSerializer.Serialize(findings, Json);
            while (json.Length > MaxFindingsJsonChars && findings.Count > 1)
            {
                findings.RemoveAt(0);
                json = JsonSerializer.Serialize(findings, Json);
            }

            row.FindingsJson = json;
        }, ct);
    }

    public async Task SetSynthesisAsync(
        int jobId, string scope, string key, string synthesis, int foldedCount, CancellationToken ct = default)
    {
        await UpsertAsync(jobId, scope, key, row =>
        {
            row.Synthesis = synthesis;
            row.SynthesizedFindingCount = foldedCount;
            row.SynthesisVersion++;
            row.SynthesizedAt = DateTimeOffset.UtcNow;
        }, ct);
    }

    public async Task MarkSynthesizedAsync(
        int jobId, string scope, string key, int foldedCount, CancellationToken ct = default)
    {
        await UpsertAsync(jobId, scope, key, row =>
        {
            // HWM only — no Synthesis text. Never LOWER the mark (a prior richer synthesis stays authoritative).
            if (foldedCount > row.SynthesizedFindingCount)
            {
                row.SynthesizedFindingCount = foldedCount;
            }
            row.SynthesizedAt = DateTimeOffset.UtcNow;
        }, ct);
    }

    public async Task<WorkContext?> GetAsync(
        int jobId, string scope, string key, CancellationToken ct = default)
    {
        using var scope0 = _scopeFactory.CreateScope();
        var db = scope0.ServiceProvider.GetRequiredService<DaleelDbContext>();
        return await db.WorkContexts.AsNoTracking()
            .FirstOrDefaultAsync(w => w.SearchJobId == jobId && w.Scope == scope && w.Key == key, ct);
    }

    public async Task<IReadOnlyList<WorkContext>> ListForJobAsync(int jobId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        return await db.WorkContexts.AsNoTracking()
            .Where(w => w.SearchJobId == jobId)
            .ToListAsync(ct);
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        // CreatedAt is a converted DateTimeOffset (Unix-ms bigint under the hood); EF applies the
        // converter to the comparison value, so this is a plain integer range delete.
        return await db.WorkContexts.Where(w => w.CreatedAt < olderThan).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Load-or-create the (jobId, scope, key) row, apply <paramref name="mutate"/>, and save. A
    /// concurrent first-insert makes SaveChanges throw a unique-violation; we catch it once, reload
    /// the now-existing row in a fresh scope, re-apply, and save — so the append/synthesis lands.
    /// </summary>
    private async Task UpsertAsync(
        int jobId, string scope, string key, Action<WorkContext> mutate, CancellationToken ct)
    {
        // Defensive column-bound clamps: identity is by (job, scope, key), so a value over the varchar
        // limit would fault every write. Clamp consistently here so the same over-long key always maps
        // to the same row (and the handler, which clamps brand keys the same way, agrees).
        scope = Clip(scope, MaxScopeChars);
        key = Clip(key, MaxKeyChars);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var dbScope = _scopeFactory.CreateScope();
            var db = dbScope.ServiceProvider.GetRequiredService<DaleelDbContext>();
            var row = await db.WorkContexts
                .FirstOrDefaultAsync(w => w.SearchJobId == jobId && w.Scope == scope && w.Key == key, ct);
            var isNew = row is null;
            if (row is null)
            {
                row = new WorkContext
                {
                    SearchJobId = jobId, Scope = scope, Key = key, CreatedAt = DateTimeOffset.UtcNow
                };
                db.WorkContexts.Add(row);
            }

            mutate(row);

            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException) when (isNew && attempt == 0)
            {
                // A concurrent execution inserted the same (jobId, scope, key) first. Retry: the next
                // loop finds the existing row and applies the mutation onto it.
            }
        }
    }

    private static string Clip(string? s, int max)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        var t = s.Trim();
        return t.Length <= max ? t : t[..max];
    }

    private static List<Finding> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<Finding>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<Finding>>(json, Json) ?? new List<Finding>();
        }
        catch (JsonException)
        {
            return new List<Finding>();
        }
    }
}
