using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>A snapshot of a user's monthly search quota.</summary>
public sealed record QuotaStatus(
    string PlanName, int Used, int? Limit, bool Unlimited, bool IsAdmin, DateTimeOffset PeriodEnd)
{
    /// <summary>Searches left this period, or null when unlimited / admin.</summary>
    public int? Remaining => Unlimited || IsAdmin ? null : Math.Max(0, (Limit ?? 0) - Used);

    /// <summary>Whether the user may run another search right now.</summary>
    public bool CanSearch => IsAdmin || Unlimited || Used < (Limit ?? 0);
}

/// <summary>
/// Enforces the per-user monthly search quota. The limit is read from the user's <em>active plan</em>
/// (defaulting to Basic), never a hardcoded number. Usage resets on the 1st of each month. Admins
/// bypass the quota entirely.
/// </summary>
public interface IQuotaService
{
    Task<QuotaStatus> GetStatusAsync(string userId, bool isAdmin, CancellationToken ct = default);

    /// <summary>Checks the quota and, if allowed, increments usage. Returns false when over limit.</summary>
    Task<bool> TryConsumeAsync(string userId, bool isAdmin, CancellationToken ct = default);
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

    public async Task<bool> TryConsumeAsync(string userId, bool isAdmin, CancellationToken ct = default)
    {
        var plan = await ResolvePlanAsync(userId, ct);
        var quota = await EnsurePeriodAsync(userId, plan, ct);
        var status = Build(plan, quota, isAdmin);

        if (!status.CanSearch)
        {
            return false;
        }

        // Admins and unlimited plans still count usage (for analytics) but are never blocked above.
        quota.SearchesUsed++;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private QuotaStatus Build(SubscriptionPlan plan, UserQuota quota, bool isAdmin) =>
        new(plan.Name, quota.SearchesUsed, plan.SearchesPerMonth, plan.IsUnlimited, isAdmin, quota.PeriodEnd);

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
                SearchesUsed = 0,
                QuotaLimit = plan.SearchesPerMonth,
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
            quota.SearchesUsed = 0;
            quota.PeriodStart = periodStart;
            quota.PeriodEnd = periodEnd;
            quota.QuotaLimit = plan.SearchesPerMonth;
            await _db.SaveChangesAsync(ct);
        }

        return quota;
    }
}
