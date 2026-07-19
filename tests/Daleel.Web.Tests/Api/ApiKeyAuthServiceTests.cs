using Daleel.Web.Api;
using Daleel.Web.Data;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daleel.Web.Tests.Api;

/// <summary>
/// The whole B2B request gate, exercised directly against real PostgreSQL (the same SQL translation
/// production runs): bearer resolution by hash, revoked-key and suspended-application rejection,
/// scope enforcement, the zero-balance hard stop (402) and the per-call ledger debit.
/// </summary>
public class ApiKeyAuthServiceTests
{
    /// <summary>Enables the kill-switch and creates an ACTIVE application on the seeded Trial plan
    /// (2,000 opening credits) with one freshly-issued read-only key.</summary>
    private static async Task<(ApiApplicationRepository Repo, ApiKeyAuthService Auth, ApiApplication App, string FullKey)>
        SetUpActiveAppAsync(PostgresTestContext ctx, string scopes = ApiScopes.DefaultReadOnly)
    {
        var config = new SystemConfigService(ctx.Db);
        await config.SetAsync("feature.api_access_enabled", "true", "bool");

        var repo = new ApiApplicationRepository(ctx.Db);
        var app = await repo.CreateAsync("acme-prod", "dev@acme.example", ApiPlan.TrialId);
        await repo.SetStatusAsync(app.Id, ApiApplication.StatusActive);
        var generated = await repo.IssueKeyAsync(app.Id, scopes);

        var auth = new ApiKeyAuthService(ctx.Db, config);
        return (repo, auth, app, generated.FullKey);
    }

    [Fact]
    public async Task ValidKey_Succeeds_AndWritesOneLedgerDebitPerCall()
    {
        using var ctx = new PostgresTestContext();
        var (_, auth, app, fullKey) = await SetUpActiveAppAsync(ctx);

        var result = await auth.AuthenticateAndChargeAsync(
            $"Bearer {fullKey}", ApiScopes.ItemsRead, "items.list", ApiPricing.ItemsListKey, 1);

        result.Succeeded.Should().BeTrue();
        result.Application!.Id.Should().Be(app.Id);

        // Metering: exactly one debit row, reason = the endpoint, amount = the charge sheet's default.
        var debits = await ctx.Db.ApiCreditLedger.AsNoTracking()
            .Where(l => l.ApplicationId == app.Id && l.Delta < 0)
            .ToListAsync();
        debits.Should().ContainSingle();
        debits[0].Delta.Should().Be(-1);
        debits[0].Reason.Should().Be("items.list");

        // Balance = opening Trial grant (2,000) minus the one debit.
        var balance = await new ApiApplicationRepository(ctx.NewContext()).BalanceAsync(app.Id);
        balance.Should().Be(2_000 - 1);
    }

    [Fact]
    public async Task ChargeAmount_ComesFromTheAdminEditableConfigRow()
    {
        using var ctx = new PostgresTestContext();
        var (_, auth, app, fullKey) = await SetUpActiveAppAsync(ctx);
        await new SystemConfigService(ctx.NewContext()).SetAsync(ApiPricing.ItemDocKey, "5", "int");

        var result = await auth.AuthenticateAndChargeAsync(
            $"Bearer {fullKey}", ApiScopes.ItemsRead, "items.get", ApiPricing.ItemDocKey, ApiPricing.ItemDocDefault);

        result.Succeeded.Should().BeTrue();
        var debit = await ctx.Db.ApiCreditLedger.AsNoTracking()
            .SingleAsync(l => l.ApplicationId == app.Id && l.Delta < 0);
        debit.Delta.Should().Be(-5);
    }

    [Fact]
    public async Task UnknownKey_Is401()
    {
        using var ctx = new PostgresTestContext();
        var (_, auth, _, _) = await SetUpActiveAppAsync(ctx);

        var result = await auth.AuthenticateAndChargeAsync(
            "Bearer dlk_live_definitely-not-a-real-key-abcdefghijklmno", ApiScopes.ItemsRead,
            "items.list", ApiPricing.ItemsListKey, 1);

        result.ErrorStatus.Should().Be(401);
        result.ErrorCode.Should().Be("invalid_key");
    }

    [Fact]
    public async Task MissingOrMalformedHeader_Is401()
    {
        using var ctx = new PostgresTestContext();
        var (_, auth, _, fullKey) = await SetUpActiveAppAsync(ctx);

        (await auth.AuthenticateAndChargeAsync(null, ApiScopes.ItemsRead, "items.list", ApiPricing.ItemsListKey, 1))
            .ErrorStatus.Should().Be(401);
        (await auth.AuthenticateAndChargeAsync(fullKey /* no "Bearer " */, ApiScopes.ItemsRead, "items.list", ApiPricing.ItemsListKey, 1))
            .ErrorStatus.Should().Be(401);
    }

    [Fact]
    public async Task RevokedKey_Is401()
    {
        using var ctx = new PostgresTestContext();
        var (repo, auth, app, fullKey) = await SetUpActiveAppAsync(ctx);
        var keyId = await ctx.Db.ApiKeys.AsNoTracking()
            .Where(k => k.ApplicationId == app.Id).Select(k => k.Id).SingleAsync();
        await repo.RevokeKeyAsync(keyId);

        var result = await auth.AuthenticateAndChargeAsync(
            $"Bearer {fullKey}", ApiScopes.ItemsRead, "items.list", ApiPricing.ItemsListKey, 1);

        // Same response as an unknown key — never reveal which keys exist(ed).
        result.ErrorStatus.Should().Be(401);
        result.ErrorCode.Should().Be("invalid_key");
    }

    [Fact]
    public async Task SuspendedApplication_Is403()
    {
        using var ctx = new PostgresTestContext();
        var (repo, auth, app, fullKey) = await SetUpActiveAppAsync(ctx);
        await repo.SetStatusAsync(app.Id, ApiApplication.StatusSuspended);

        var result = await auth.AuthenticateAndChargeAsync(
            $"Bearer {fullKey}", ApiScopes.ItemsRead, "items.list", ApiPricing.ItemsListKey, 1);

        result.ErrorStatus.Should().Be(403);
        result.ErrorCode.Should().Be("application_suspended");
    }

    [Fact]
    public async Task PendingApplication_Is403_UntilApproved()
    {
        using var ctx = new PostgresTestContext();
        var (repo, auth, app, fullKey) = await SetUpActiveAppAsync(ctx);
        await repo.SetStatusAsync(app.Id, ApiApplication.StatusPending);

        var result = await auth.AuthenticateAndChargeAsync(
            $"Bearer {fullKey}", ApiScopes.ItemsRead, "items.list", ApiPricing.ItemsListKey, 1);

        result.ErrorStatus.Should().Be(403);
    }

    [Fact]
    public async Task MissingScope_Is403_AndNothingIsCharged()
    {
        using var ctx = new PostgresTestContext();
        var (_, auth, app, fullKey) = await SetUpActiveAppAsync(ctx, scopes: ApiScopes.StoresRead);

        var result = await auth.AuthenticateAndChargeAsync(
            $"Bearer {fullKey}", ApiScopes.ItemsRead, "items.list", ApiPricing.ItemsListKey, 1);

        result.ErrorStatus.Should().Be(403);
        result.ErrorCode.Should().Be("missing_scope");
        (await ctx.Db.ApiCreditLedger.AsNoTracking().CountAsync(l => l.ApplicationId == app.Id && l.Delta < 0))
            .Should().Be(0);
    }

    [Fact]
    public async Task ZeroBalance_Is402_AndNoFurtherDebitIsWritten()
    {
        using var ctx = new PostgresTestContext();
        var (repo, auth, app, fullKey) = await SetUpActiveAppAsync(ctx);
        // Claw back the opening grant so the balance is exactly zero.
        await repo.GrantCreditsAsync(app.Id, -2_000, "test.drain");

        var result = await auth.AuthenticateAndChargeAsync(
            $"Bearer {fullKey}", ApiScopes.ItemsRead, "items.list", ApiPricing.ItemsListKey, 1);

        result.ErrorStatus.Should().Be(402);
        result.ErrorCode.Should().Be("insufficient_credits");
        (await repo.BalanceAsync(app.Id)).Should().Be(0); // the rejected call charged nothing
    }

    [Fact]
    public async Task KillSwitchOff_Is403_ForEveryone()
    {
        using var ctx = new PostgresTestContext();
        var (_, _, _, fullKey) = await SetUpActiveAppAsync(ctx);
        var config = new SystemConfigService(ctx.NewContext());
        await config.SetAsync("feature.api_access_enabled", "false", "bool");

        var auth = new ApiKeyAuthService(ctx.NewContext(), config);
        var result = await auth.AuthenticateAndChargeAsync(
            $"Bearer {fullKey}", ApiScopes.ItemsRead, "items.list", ApiPricing.ItemsListKey, 1);

        result.ErrorStatus.Should().Be(403);
        result.ErrorCode.Should().Be("api_disabled");
    }

    [Fact]
    public async Task SuccessfulCall_StampsLastUsedAt()
    {
        using var ctx = new PostgresTestContext();
        var (_, auth, app, fullKey) = await SetUpActiveAppAsync(ctx);

        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        await auth.AuthenticateAndChargeAsync(
            $"Bearer {fullKey}", ApiScopes.ItemsRead, "items.list", ApiPricing.ItemsListKey, 1);

        var key = await ctx.NewContext().ApiKeys.AsNoTracking()
            .SingleAsync(k => k.ApplicationId == app.Id);
        key.LastUsedAt.Should().NotBeNull();
        key.LastUsedAt!.Value.Should().BeAfter(before);
    }
}
