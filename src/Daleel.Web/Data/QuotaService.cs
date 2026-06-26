using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>A snapshot of a user's monthly CREDIT balance. (Field names are generic — Used/Limit/Remaining
/// — but the unit is credits: each search charges a variable amount based on the provider calls it made.)</summary>
public sealed record QuotaStatus(
    string PlanName, int Used, int? Limit, bool Unlimited, bool IsAdmin, DateTimeOffset PeriodEnd)
{
    /// <summary>Credits left this period, or null when unlimited / admin.</summary>
    public int? Remaining => Unlimited || IsAdmin ? null : Math.Max(0, (Limit ?? 0) - Used);

    /// <summary>Whether the user may START another search right now (i.e. has any credits left).</summary>
    public bool CanSearch => IsAdmin || Unlimited || Used < (Limit ?? 0);
}

/// <summary>
/// Enforces the per-user monthly CREDIT balance. The allowance is read from the user's <em>active plan</em>
/// (<see cref="SubscriptionPlan.MonthlyCredits"/>, defaulting to Basic), never a hardcoded number. Usage
/// resets on the 1st of each month. Admins bypass the gate entirely.
/// </summary>
/// <remarks>
/// Credits are charged AFTER a search finishes (its cost isn't known until the provider calls have run —
/// see <see cref="CreditCost"/>), via <see cref="ChargeCreditsAsync"/>. The pre-search gate is just
/// <see cref="QuotaStatus.CanSearch"/>: you may start a search while you have any credits left. A search
/// already in flight is never killed mid-way for going slightly over — the next one is blocked instead.
/// </remarks>
public interface IQuotaService
{
    Task<QuotaStatus> GetStatusAsync(string userId, bool isAdmin, CancellationToken ct = default);

    /// <summary>Charges a finished search's actual credit cost against the user's current period.</summary>
    Task ChargeCreditsAsync(string userId, int credits, CancellationToken ct = default);
}

public sealed class QuotaService : IQuotaService
{
    private readonly DaleelDbContext _db;
    private readonly Func<DateTimeOffset> _clock;

    public QuotaService(DaleelDbContext db, Func<DateTimeOffset>? clock = null)
    {
        _db = db;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<QuotaStatus> GetStatusAsync(string userId, bool isAdmin, CancellationToken ct = default)
    {
        var plan = await ResolvePlanAsync(userId, ct);
        var quota = await EnsurePeriodAsync(userId, plan, ct);
        return Build(plan, quota, isAdmin);
    }

    public async Task ChargeCreditsAsync(string userId, int credits, CancellationToken ct = default)
    {
        if (credits <= 0)
        {
            return; // cache hits (and zero-cost runs) charge nothing
        }

        var plan = await ResolvePlanAsync(userId, ct);
        await EnsurePeriodAsync(userId, plan, ct); // ensure a current-period row exists

        // Atomic increment — concurrent searches for the same user each add their own cost without a
        // read-modify-write race. We don't block on the limit here: the cost is already incurred, and
        // the next search's CanSearch pre-check is what actually gates an out-of-credits user.
        await _db.UserQuotas
            .Where(q => q.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.CreditsUsed, q => q.CreditsUsed + credits), ct);

        // ExecuteUpdate bypasses the change tracker; refresh any tracked row so a follow-up
        // GetStatusAsync on this same scoped context reflects the new balance.
        var tracked = _db.ChangeTracker.Entries<UserQuota>().FirstOrDefault(e => e.Entity.UserId == userId);
        if (tracked is not null)
        {
            await tracked.ReloadAsync(ct);
        }
    }

    private QuotaStatus Build(SubscriptionPlan plan, UserQuota quota, bool isAdmin) =>
        new(plan.Name, quota.CreditsUsed, plan.MonthlyCredits, plan.IsUnlimited, isAdmin, quota.PeriodEnd);

    /// <summary>The user's active plan, or Basic when they have no live subscription.</summary>
    private async Task<SubscriptionPlan> ResolvePlanAsync(string userId, CancellationToken ct)
    {
        var now = _clock();
        // SQLite can't translate DateTimeOffset comparison/ordering, so filter+order in memory over
        // the (tiny) set of a user's active subscriptions.
        var active = await _db.UserSubscriptions
            .Include(s => s.Plan)
            .Where(s => s.UserId == userId && s.Status == "active")
            .ToListAsync(ct);

        var sub = active
            .Where(s => s.ExpiresAt == null || s.ExpiresAt > now)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefault();

        if (sub?.Plan is { } plan)
        {
            return plan;
        }

        return await _db.SubscriptionPlans.FirstAsync(p => p.Id == SubscriptionPlan.BasicId, ct);
    }

    /// <summary>Loads (or creates) the user's quota row, rolling it over when the month changes.</summary>
    private async Task<UserQuota> EnsurePeriodAsync(string userId, SubscriptionPlan plan, CancellationToken ct)
    {
        var now = _clock();
        var periodStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = periodStart.AddMonths(1);

        var quota = await _db.UserQuotas.FirstOrDefaultAsync(q => q.UserId == userId, ct);
        if (quota is null)
        {
            quota = new UserQuota
            {
                UserId = userId,
                CreditsUsed = 0,
                QuotaLimit = plan.MonthlyCredits,
                PeriodStart = periodStart,
                PeriodEnd = periodEnd
            };
            _db.UserQuotas.Add(quota);
            await _db.SaveChangesAsync(ct);
            return quota;
        }

        // New month → reset the counter and re-resolve the (possibly changed) plan limit.
        if (now >= quota.PeriodEnd)
        {
            quota.CreditsUsed = 0;
            quota.SearchesUsed = 0;
            quota.PeriodStart = periodStart;
            quota.PeriodEnd = periodEnd;
            quota.QuotaLimit = plan.MonthlyCredits;
            await _db.SaveChangesAsync(ct);
        }

        return quota;
    }
}
