using System.Text.Json;
using Daleel.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Pipeline.Enrichment;

/// <summary>
/// Queue operations over the <see cref="EnrichmentWorkItem"/> table. Enqueue is used by the search
/// worker (dispatch) and by handlers (follow-up fan-out); claim/finish belong to the consumer loop.
/// Every method opens its own scope — callers may be singletons.
/// </summary>
public interface IEnrichmentWorkQueue
{
    /// <summary>Inserts units in one batch. Follow-up fan-out goes through this too.</summary>
    Task EnqueueAsync(IReadOnlyList<EnrichmentWorkItem> items, CancellationToken ct = default);

    /// <summary>
    /// Atomically claims up to <paramref name="max"/> eligible units (pending and due, or running
    /// with an expired lease) and flips them to running with a fresh lease. FOR UPDATE SKIP LOCKED —
    /// no unit is ever handed to two consumers, across containers included.
    /// </summary>
    Task<IReadOnlyList<EnrichmentWorkItem>> ClaimAsync(int max, TimeSpan lease, CancellationToken ct = default);

    /// <summary>Marks a claimed unit done.</summary>
    Task CompleteAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Returns a claimed unit to the queue for a later attempt, or kills it when attempts are
    /// exhausted. The reason is stored either way — give-ups must be visible, never silent.
    /// </summary>
    Task RetryAsync(long id, string reason, TimeSpan? delay = null, CancellationToken ct = default);

    /// <summary>Kills a claimed unit immediately (non-retryable give-up, e.g. the job's cost cap).</summary>
    Task KillAsync(long id, string reason, CancellationToken ct = default);

    /// <summary>Pending+running units for a job — the UI's "deep dive still in progress" signal.</summary>
    Task<int> OpenCountAsync(int searchJobId, CancellationToken ct = default);

    /// <summary>
    /// Deads running rows whose lease expired AND whose attempts are exhausted — units that crashed
    /// the container before they could write an outcome. Returns how many were reaped. The claim
    /// query already refuses to re-run them; this makes them VISIBLE (dead, with a reason) rather
    /// than silently stuck Running forever.
    /// </summary>
    Task<int> ReapExhaustedAsync(CancellationToken ct = default);
}

public sealed class EnrichmentWorkQueue : IEnrichmentWorkQueue
{
    private readonly IServiceScopeFactory _scopeFactory;

    public EnrichmentWorkQueue(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    /// <summary>Serializes a payload record with the compact camelCase wire shape units share.</summary>
    public static string Payload<T>(T payload) =>
        JsonSerializer.Serialize(payload, PayloadJson);

    public static T? ReadPayload<T>(string json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, PayloadJson);

    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task EnqueueAsync(IReadOnlyList<EnrichmentWorkItem> items, CancellationToken ct = default)
    {
        if (items.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            item.CreatedAt = now;
            if (item.NotBefore == default)
            {
                item.NotBefore = now;
            }
        }

        db.EnrichmentWorkItems.AddRange(items);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<EnrichmentWorkItem>> ClaimAsync(
        int max, TimeSpan lease, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Timestamps are Unix-ms bigints (DbContext converters), so eligibility is integer math.
        // An expired lease on a running row is the crash-recovery path: the container that claimed
        // it died (or was deployed over), and the unit becomes claimable again — but ONLY while it
        // still has attempts left. Without the "Attempts" < "MaxAttempts" bound, a unit that crashes
        // the container BEFORE it can write a Retry/Kill outcome (OOM, a handler ignoring the budget
        // token) re-claims on every lease expiry forever, Attempts climbing unbounded past the cap
        // that was meant to stop it. ReapExhaustedAsync (called by the consumer) Deads those rows so
        // they surface instead of looping.
        var claimed = await db.EnrichmentWorkItems
            .FromSqlRaw(
                """
                SELECT * FROM "EnrichmentWorkItems"
                WHERE ("Status" = {0} AND "NotBefore" <= {2})
                   OR ("Status" = {1} AND "LeaseUntil" IS NOT NULL AND "LeaseUntil" < {2}
                       AND "Attempts" < "MaxAttempts")
                ORDER BY "Id"
                LIMIT {3}
                FOR UPDATE SKIP LOCKED
                """,
                WorkItemStatus.Pending, WorkItemStatus.Running, nowMs, max)
            .ToListAsync(ct);

        if (claimed.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return Array.Empty<EnrichmentWorkItem>();
        }

        var leaseUntil = DateTimeOffset.UtcNow + lease;
        foreach (var item in claimed)
        {
            item.Status = WorkItemStatus.Running;
            item.Attempts++;
            item.LeaseUntil = leaseUntil;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return claimed;
    }

    public Task CompleteAsync(long id, CancellationToken ct = default) =>
        MutateAsync(id, item =>
        {
            item.Status = WorkItemStatus.Done;
            item.CompletedAt = DateTimeOffset.UtcNow;
            item.LeaseUntil = null;
        }, ct);

    public Task RetryAsync(long id, string reason, TimeSpan? delay = null, CancellationToken ct = default) =>
        MutateAsync(id, item =>
        {
            item.LastError = Truncate(reason);
            item.LeaseUntil = null;
            if (item.Attempts >= item.MaxAttempts)
            {
                item.Status = WorkItemStatus.Dead;
                item.CompletedAt = DateTimeOffset.UtcNow;
                return;
            }

            item.Status = WorkItemStatus.Pending;
            // Default backoff scales with attempts (30s, 60s, 90s…, capped) — enough for a flaky
            // provider to recover without parking the unit for ages.
            item.NotBefore = DateTimeOffset.UtcNow +
                (delay ?? TimeSpan.FromSeconds(Math.Min(300, 30 * item.Attempts)));
        }, ct);

    public Task KillAsync(long id, string reason, CancellationToken ct = default) =>
        MutateAsync(id, item =>
        {
            item.Status = WorkItemStatus.Dead;
            item.LastError = Truncate(reason);
            item.CompletedAt = DateTimeOffset.UtcNow;
            item.LeaseUntil = null;
        }, ct);

    public async Task<int> OpenCountAsync(int searchJobId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        return await db.EnrichmentWorkItems
            .CountAsync(i => i.SearchJobId == searchJobId &&
                (i.Status == WorkItemStatus.Pending || i.Status == WorkItemStatus.Running), ct);
    }

    public async Task<int> ReapExhaustedAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        // LeaseUntil is a converted DateTimeOffset? (Unix-ms bigint under the hood); EF applies the
        // converter to the comparison value, so a direct DateTimeOffset compare is a bigint compare.
        var now = DateTimeOffset.UtcNow;
        return await db.EnrichmentWorkItems
            .Where(i => i.Status == WorkItemStatus.Running &&
                        i.LeaseUntil != null && i.LeaseUntil < now &&
                        i.Attempts >= i.MaxAttempts)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, WorkItemStatus.Dead)
                .SetProperty(i => i.LastError, "attempts exhausted before an outcome (crash/lease-expiry)")
                .SetProperty(i => i.CompletedAt, now), ct);
    }

    private async Task MutateAsync(long id, Action<EnrichmentWorkItem> mutate, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        var item = await db.EnrichmentWorkItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null)
        {
            return;
        }

        mutate(item);
        await db.SaveChangesAsync(ct);
    }

    private static string Truncate(string reason) =>
        reason.Length <= 1000 ? reason : reason[..1000]; // LastError column cap
}
