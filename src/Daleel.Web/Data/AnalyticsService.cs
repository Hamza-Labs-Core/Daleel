using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>Headline counts for the admin dashboard.</summary>
public sealed record AdminDashboardStats(
    int UsersTotal, int UsersToday, int UsersThisWeek, int UsersThisMonth,
    int SearchesToday, int SearchesThisWeek, int SearchesThisMonth,
    IReadOnlyList<(string Plan, int Count)> PlanBreakdown,
    IReadOnlyList<(string Query, int Count)> TopQueries,
    IReadOnlyList<(string UserId, int Count)> TopUsers);

/// <summary>Records and aggregates analytics. Append-only; never exposes user-owned rows.</summary>
public interface IAnalyticsService
{
    Task RecordSearchAsync(AnalyticsEvent ev, CancellationToken ct = default);
    Task RecordLoginAsync(string userId, string? provider, string? ip, CancellationToken ct = default);
    Task RecordPageViewAsync(string path, string? userId, CancellationToken ct = default);

    Task<AdminDashboardStats> GetDashboardAsync(CancellationToken ct = default);
    Task<IReadOnlyList<(string Type, int Count)>> SearchesByTypeAsync(DateTime since, CancellationToken ct = default);
    Task<IReadOnlyList<(DateTime Day, int Count)>> SearchesPerDayAsync(int days, CancellationToken ct = default);
    Task<IReadOnlyList<(string Geo, int Count)>> GeoDistributionAsync(DateTime since, CancellationToken ct = default);
    Task<(int TotalFiltered, int SearchesWithFilters, IReadOnlyList<(string Category, int Count)> ByCategory)>
        ModerationStatsAsync(DateTime since, CancellationToken ct = default);

    /// <summary>One-way truncated hash of an IP, so analytics never stores the raw address.</summary>
    static string? HashIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ip));
        return Convert.ToHexString(bytes)[..16];
    }
}

public sealed class AnalyticsService : IAnalyticsService
{
    private readonly DaleelDbContext _db;
    private readonly Func<DateTime> _clock;

    public AnalyticsService(DaleelDbContext db, Func<DateTime>? clock = null)
    {
        _db = db;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public async Task RecordSearchAsync(AnalyticsEvent ev, CancellationToken ct = default)
    {
        ev.EventType = "search";
        ev.Timestamp = _clock();
        _db.AnalyticsEvents.Add(ev);
        await _db.SaveChangesAsync(ct);
    }

    public async Task RecordLoginAsync(string userId, string? provider, string? ip, CancellationToken ct = default)
    {
        _db.AnalyticsEvents.Add(new AnalyticsEvent
        {
            EventType = "login", UserId = userId, Provider = provider,
            IpHash = IAnalyticsService.HashIp(ip), Timestamp = _clock()
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task RecordPageViewAsync(string path, string? userId, CancellationToken ct = default)
    {
        _db.AnalyticsEvents.Add(new AnalyticsEvent
        {
            EventType = "pageview", Path = path, UserId = userId, Timestamp = _clock()
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<AdminDashboardStats> GetDashboardAsync(CancellationToken ct = default)
    {
        var now = _clock();
        var today = now.Date;
        var weekAgo = today.AddDays(-7);
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var users = _db.Users.Cast<ApplicationUser>();
        var usersTotal = await users.CountAsync(ct);
        var usersToday = await users.CountAsync(u => u.CreatedAt >= today, ct);
        var usersWeek = await users.CountAsync(u => u.CreatedAt >= weekAgo, ct);
        var usersMonth = await users.CountAsync(u => u.CreatedAt >= monthStart, ct);

        var searches = _db.AnalyticsEvents.Where(e => e.EventType == "search");
        var sToday = await searches.CountAsync(e => e.Timestamp >= today, ct);
        var sWeek = await searches.CountAsync(e => e.Timestamp >= weekAgo, ct);
        var sMonth = await searches.CountAsync(e => e.Timestamp >= monthStart, ct);

        var plans = await _db.UserSubscriptions
            .Include(s => s.Plan)
            .Where(s => s.Status == "active")
            .ToListAsync(ct);
        var planBreakdown = plans
            .GroupBy(s => s.Plan!.Name)
            .Select(g => (g.Key, g.Count()))
            .ToList();
        // Everyone without an active subscription is on Basic.
        var basicCount = usersTotal - plans.Select(p => p.UserId).Distinct().Count();
        if (basicCount > 0)
        {
            planBreakdown.Insert(0, ("Basic", basicCount));
        }

        var monthSearches = await searches
            .Where(e => e.Timestamp >= monthStart)
            .Select(e => new { e.Query, e.UserId })
            .ToListAsync(ct);

        var topQueries = monthSearches
            .Where(e => !string.IsNullOrWhiteSpace(e.Query))
            .GroupBy(e => e.Query!)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(t => t.Item2)
            .Take(10)
            .ToList();

        var topUsers = monthSearches
            .Where(e => e.UserId is not null)
            .GroupBy(e => e.UserId!)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(t => t.Item2)
            .Take(10)
            .ToList();

        return new AdminDashboardStats(usersTotal, usersToday, usersWeek, usersMonth,
            sToday, sWeek, sMonth, planBreakdown, topQueries, topUsers);
    }

    public async Task<IReadOnlyList<(string Type, int Count)>> SearchesByTypeAsync(DateTime since, CancellationToken ct = default)
    {
        var rows = await _db.AnalyticsEvents
            .Where(e => e.EventType == "search" && e.Timestamp >= since && e.QueryType != null)
            .Select(e => e.QueryType!)
            .ToListAsync(ct);
        return rows.GroupBy(t => t).Select(g => (g.Key, g.Count()))
            .OrderByDescending(t => t.Item2).ToList();
    }

    public async Task<IReadOnlyList<(DateTime Day, int Count)>> SearchesPerDayAsync(int days, CancellationToken ct = default)
    {
        var since = _clock().Date.AddDays(-days + 1);
        var rows = await _db.AnalyticsEvents
            .Where(e => e.EventType == "search" && e.Timestamp >= since)
            .Select(e => e.Timestamp)
            .ToListAsync(ct);
        var byDay = rows.GroupBy(t => t.Date).ToDictionary(g => g.Key, g => g.Count());
        return Enumerable.Range(0, days)
            .Select(i => since.AddDays(i))
            .Select(d => (d, byDay.GetValueOrDefault(d, 0)))
            .ToList();
    }

    public async Task<IReadOnlyList<(string Geo, int Count)>> GeoDistributionAsync(DateTime since, CancellationToken ct = default)
    {
        var rows = await _db.AnalyticsEvents
            .Where(e => e.EventType == "search" && e.Timestamp >= since && e.Geo != null)
            .Select(e => e.Geo!)
            .ToListAsync(ct);
        return rows.GroupBy(g => g).Select(g => (g.Key, g.Count()))
            .OrderByDescending(t => t.Item2).ToList();
    }

    public async Task<(int TotalFiltered, int SearchesWithFilters, IReadOnlyList<(string Category, int Count)> ByCategory)>
        ModerationStatsAsync(DateTime since, CancellationToken ct = default)
    {
        var rows = await _db.AnalyticsEvents
            .Where(e => e.EventType == "search" && e.Timestamp >= since && e.FilteredCount != null)
            .Select(e => new { e.FilteredCount, e.FilteredCategories })
            .ToListAsync(ct);

        var total = rows.Sum(r => r.FilteredCount ?? 0);
        var withFilters = rows.Count(r => (r.FilteredCount ?? 0) > 0);
        var byCategory = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.FilteredCategories))
            .SelectMany(r => r.FilteredCategories!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .GroupBy(c => c)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(t => t.Item2)
            .ToList();

        return (total, withFilters, byCategory);
    }
}
