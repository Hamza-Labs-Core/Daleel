using Daleel.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Api;

/// <summary>The scope tokens a key can carry (read-only surface for now).</summary>
public static class ApiScopes
{
    public const string ItemsRead = "items:read";
    public const string StoresRead = "stores:read";
    public const string BrandsRead = "brands:read";

    /// <summary>Default scope set for a freshly-issued key: all reads (keys default to read-only).</summary>
    public const string DefaultReadOnly = $"{ItemsRead},{StoresRead},{BrandsRead}";
}

/// <summary>
/// The admin-editable per-action credit charge sheet — SystemConfig keys (like the existing
/// <c>pricing.*</c> rows) with the spec's defaults as fallbacks.
/// </summary>
public static class ApiPricing
{
    public const string ItemsListKey = "pricing.api.items_list";
    public const string ItemDocKey = "pricing.api.item_doc";
    public const string StoresKey = "pricing.api.stores";
    public const string BrandsKey = "pricing.api.brands";

    public const int ItemsListDefault = 1;
    public const int ItemDocDefault = 2;
    public const int StoresDefault = 1;
    public const int BrandsDefault = 1;
}

/// <summary>
/// Outcome of authenticating (and metering) one API request. Exactly one of the two shapes:
/// an error (<see cref="ErrorStatus"/> + <see cref="ErrorCode"/>) or a success carrying the
/// resolved application.
/// </summary>
public sealed record ApiAuthResult(
    int? ErrorStatus,
    string? ErrorCode,
    ApiApplication? Application,
    ApiKey? Key)
{
    public bool Succeeded => ErrorStatus is null;

    public static ApiAuthResult Fail(int status, string code) => new(status, code, null, null);
    public static ApiAuthResult Ok(ApiApplication app, ApiKey key) => new(null, null, app, key);
}

/// <summary>
/// The whole B2B request gate in one call: resolve the bearer key by SHA-256 hash, reject
/// revoked keys / non-active applications, enforce the endpoint's scope, check the credit balance
/// (SUM of the ledger) and debit the endpoint's charge. Resolved from the REQUEST scope (transient,
/// like every repository) so each request gets its own DbContext.
/// </summary>
public interface IApiKeyAuthService
{
    /// <param name="authorizationHeader">The raw <c>Authorization</c> header value ("Bearer dlk_live_…").</param>
    /// <param name="requiredScope">Scope this endpoint demands, e.g. <see cref="ApiScopes.ItemsRead"/>.</param>
    /// <param name="endpointName">Ledger reason for the debit, e.g. "items.list".</param>
    /// <param name="pricingKey">SystemConfig key holding the charge, e.g. <see cref="ApiPricing.ItemsListKey"/>.</param>
    /// <param name="defaultCharge">Fallback charge when the config row is absent.</param>
    Task<ApiAuthResult> AuthenticateAndChargeAsync(
        string? authorizationHeader, string requiredScope, string endpointName,
        string pricingKey, int defaultCharge, CancellationToken ct = default);
}

public sealed class ApiKeyAuthService : IApiKeyAuthService
{
    private const string BearerPrefix = "Bearer ";

    private readonly DaleelDbContext _db;
    private readonly ISystemConfigService _config;
    private readonly ILogger<ApiKeyAuthService>? _logger;

    public ApiKeyAuthService(DaleelDbContext db, ISystemConfigService config, ILogger<ApiKeyAuthService>? logger = null)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    public async Task<ApiAuthResult> AuthenticateAndChargeAsync(
        string? authorizationHeader, string requiredScope, string endpointName,
        string pricingKey, int defaultCharge, CancellationToken ct = default)
    {
        // Global kill-switch (spec: feature.api_access_enabled stays the gate). Default OFF — the
        // API only serves once an admin flips it, so a fresh deployment never exposes it by accident.
        if (!await _config.GetBoolAsync("feature.api_access_enabled", false, ct))
        {
            return ApiAuthResult.Fail(StatusCodes.Status403Forbidden, "api_disabled");
        }

        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ApiAuthResult.Fail(StatusCodes.Status401Unauthorized, "missing_key");
        }

        var presented = authorizationHeader[BearerPrefix.Length..].Trim();
        if (!presented.StartsWith(ApiKeyGenerator.LivePrefix, StringComparison.Ordinal))
        {
            return ApiAuthResult.Fail(StatusCodes.Status401Unauthorized, "invalid_key");
        }

        // Resolve by hash — the only stored form of the key. Tracked (not AsNoTracking) so the
        // best-effort LastUsedAt stamp below can ride the same context.
        var hash = ApiKeyGenerator.Hash(presented);
        var key = await _db.ApiKeys
            .Include(k => k.Application!).ThenInclude(a => a.Plan)
            .FirstOrDefaultAsync(k => k.Hash == hash, ct);
        if (key is null || key.RevokedAt is not null)
        {
            // Identical response for unknown and revoked — never reveal which keys exist(ed).
            return ApiAuthResult.Fail(StatusCodes.Status401Unauthorized, "invalid_key");
        }

        var app = key.Application!;
        if (app.Status != ApiApplication.StatusActive)
        {
            // Suspended (or still-pending) application: the key is real but the org may not call.
            return ApiAuthResult.Fail(StatusCodes.Status403Forbidden, "application_suspended");
        }

        if (!key.ScopeList().Contains(requiredScope, StringComparer.OrdinalIgnoreCase))
        {
            return ApiAuthResult.Fail(StatusCodes.Status403Forbidden, "missing_scope");
        }

        // Hard-stop at zero credits (402). Balance is SUM(Delta) — append-only, no cached column.
        var balance = await _db.ApiCreditLedger
            .Where(l => l.ApplicationId == app.Id)
            .SumAsync(l => (long?)l.Delta, ct) ?? 0;
        if (balance <= 0)
        {
            return ApiAuthResult.Fail(StatusCodes.Status402PaymentRequired, "insufficient_credits");
        }

        // Meter the call: one debit row per request, reason = the endpoint. The charge amount is the
        // admin-editable pricing.api.* row so it can be tuned without a deploy.
        var charge = await _config.GetIntAsync(pricingKey, defaultCharge, ct);
        if (charge > 0)
        {
            _db.ApiCreditLedger.Add(new ApiCreditLedger
            {
                ApplicationId = app.Id,
                Delta = -charge,
                Reason = endpointName,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        // LastUsedAt is telemetry, not truth — best-effort by design. It shares the SaveChanges with
        // the debit; if that write fails the REQUEST still proceeds (metering must degrade, never
        // fault a read), and the next successful call re-stamps it anyway.
        key.LastUsedAt = DateTimeOffset.UtcNow;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "B2B metering write failed for application {AppId} on {Endpoint}", app.Id, endpointName);
        }

        return ApiAuthResult.Ok(app, key);
    }
}
