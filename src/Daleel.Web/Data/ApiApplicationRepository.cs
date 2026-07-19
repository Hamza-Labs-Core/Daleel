using Daleel.Web.Api;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>One /admin/api table row: the application with its plan, keys and current credit balance.</summary>
public sealed record ApiApplicationSummary(
    ApiApplication Application,
    IReadOnlyList<ApiKey> Keys,
    long CreditBalance);

/// <summary>
/// Persistence + admin operations for B2B <see cref="ApiApplication"/>s, their keys and their credit
/// ledger. Transient over the transient <see cref="DaleelDbContext"/>, like every repository.
/// </summary>
public interface IApiApplicationRepository
{
    /// <summary>All applications with plan, keys and balance — the /admin/api table, newest first.</summary>
    Task<IReadOnlyList<ApiApplicationSummary>> ListAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ApiPlan>> ListPlansAsync(CancellationToken ct = default);

    /// <summary>Creates a pending application on the given plan and grants its first month of plan
    /// credits (so approval is the only step between creation and a working key).</summary>
    Task<ApiApplication> CreateAsync(string name, string contactEmail, int apiPlanId, CancellationToken ct = default);

    /// <summary>Sets the lifecycle status (pending/active/suspended).</summary>
    Task SetStatusAsync(int applicationId, string status, CancellationToken ct = default);

    /// <summary>Issues a new key for the application. Returns the FULL key — the only time it exists
    /// in plaintext; only hash + display prefix are stored.</summary>
    Task<GeneratedApiKey> IssueKeyAsync(int applicationId, string scopes, CancellationToken ct = default);

    Task RevokeKeyAsync(int keyId, CancellationToken ct = default);

    /// <summary>Appends an admin adjustment row to the ledger (positive = grant, negative = claw-back).</summary>
    Task GrantCreditsAsync(int applicationId, long delta, string reason, CancellationToken ct = default);

    /// <summary>The application's current balance: SUM(Delta) over its ledger.</summary>
    Task<long> BalanceAsync(int applicationId, CancellationToken ct = default);
}

public sealed class ApiApplicationRepository : IApiApplicationRepository
{
    private readonly DaleelDbContext _db;

    public ApiApplicationRepository(DaleelDbContext db) => _db = db;

    public async Task<IReadOnlyList<ApiApplicationSummary>> ListAsync(CancellationToken ct = default)
    {
        var apps = await _db.ApiApplications.AsNoTracking()
            .Include(a => a.Plan)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        var keys = await _db.ApiKeys.AsNoTracking()
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);

        // One grouped aggregate for every balance — never a SUM query per row.
        var balances = await _db.ApiCreditLedger.AsNoTracking()
            .GroupBy(l => l.ApplicationId)
            .Select(g => new { g.Key, Balance = g.Sum(l => l.Delta) })
            .ToDictionaryAsync(x => x.Key, x => x.Balance, ct);

        return apps.Select(a => new ApiApplicationSummary(
                a,
                keys.Where(k => k.ApplicationId == a.Id).ToList(),
                balances.GetValueOrDefault(a.Id)))
            .ToList();
    }

    public async Task<IReadOnlyList<ApiPlan>> ListPlansAsync(CancellationToken ct = default) =>
        await _db.ApiPlans.AsNoTracking().OrderBy(p => p.MonthlyPriceUsd).ToListAsync(ct);

    public async Task<ApiApplication> CreateAsync(
        string name, string contactEmail, int apiPlanId, CancellationToken ct = default)
    {
        var app = new ApiApplication
        {
            Name = name.Trim(),
            ContactEmail = contactEmail.Trim(),
            ApiPlanId = apiPlanId,
            Status = ApiApplication.StatusPending,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.ApiApplications.Add(app);
        await _db.SaveChangesAsync(ct);

        // Opening grant: the plan's monthly credits, as a ledger row like every other movement.
        // (Recurring period grants are a later step — invoice-first billing starts manual anyway.)
        var plan = await _db.ApiPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == apiPlanId, ct);
        if (plan is { MonthlyApiCredits: > 0 })
        {
            _db.ApiCreditLedger.Add(new ApiCreditLedger
            {
                ApplicationId = app.Id,
                Delta = plan.MonthlyApiCredits,
                Reason = "grant.plan",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }

        return app;
    }

    public async Task SetStatusAsync(int applicationId, string status, CancellationToken ct = default)
    {
        if (status is not (ApiApplication.StatusPending or ApiApplication.StatusActive or ApiApplication.StatusSuspended))
        {
            throw new ArgumentException($"Unknown application status '{status}'.", nameof(status));
        }

        var app = await _db.ApiApplications.FirstOrDefaultAsync(a => a.Id == applicationId, ct)
                  ?? throw new InvalidOperationException($"Application {applicationId} not found.");
        app.Status = status;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<GeneratedApiKey> IssueKeyAsync(
        int applicationId, string scopes, CancellationToken ct = default)
    {
        var generated = ApiKeyGenerator.Generate();
        _db.ApiKeys.Add(new ApiKey
        {
            ApplicationId = applicationId,
            Hash = generated.Hash,
            Prefix = generated.Prefix,
            Scopes = string.IsNullOrWhiteSpace(scopes) ? ApiScopes.DefaultReadOnly : scopes.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        return generated;
    }

    public async Task RevokeKeyAsync(int keyId, CancellationToken ct = default)
    {
        var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct)
                  ?? throw new InvalidOperationException($"API key {keyId} not found.");
        key.RevokedAt ??= DateTimeOffset.UtcNow; // idempotent — a second revoke keeps the first stamp
        await _db.SaveChangesAsync(ct);
    }

    public async Task GrantCreditsAsync(
        int applicationId, long delta, string reason, CancellationToken ct = default)
    {
        _db.ApiCreditLedger.Add(new ApiCreditLedger
        {
            ApplicationId = applicationId,
            Delta = delta,
            Reason = string.IsNullOrWhiteSpace(reason) ? "grant.admin" : reason.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<long> BalanceAsync(int applicationId, CancellationToken ct = default) =>
        await _db.ApiCreditLedger
            .Where(l => l.ApplicationId == applicationId)
            .SumAsync(l => (long?)l.Delta, ct) ?? 0;
}
