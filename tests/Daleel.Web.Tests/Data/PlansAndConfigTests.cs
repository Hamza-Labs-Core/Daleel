using Daleel.Web.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daleel.Web.Tests.Data;

public class PlansAndConfigTests
{
    [Fact]
    public async Task ThreeActivePlans_AreSeeded_ForPricingPage()
    {
        using var ctx = new PostgresTestContext();

        var plans = await ctx.Db.SubscriptionPlans
            .Where(p => p.IsActive)
            .OrderBy(p => p.SortOrder)
            .ToListAsync();

        plans.Select(p => p.Name).Should().Equal("Basic", "Pro", "Unlimited");
        plans[0].SearchesPerMonth.Should().Be(5);
        plans[0].PriceMonthly.Should().Be(0m);
        plans[1].SearchesPerMonth.Should().Be(50);
        plans[1].PriceMonthly.Should().Be(9.99m);
        plans[2].SearchesPerMonth.Should().Be(250);
        plans[2].PriceMonthly.Should().Be(100m);
    }

    [Fact]
    public async Task ChangingUserToPro_RaisesTheirQuotaLimit()
    {
        using var ctx = new PostgresTestContext();
        var now = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var quota = new QuotaService(ctx.Db, () => now);

        (await quota.GetStatusAsync("user", false)).Limit.Should().Be(500); // Basic credits by default

        // Admin "changes the plan" → an active Pro subscription.
        ctx.Db.UserSubscriptions.Add(new UserSubscription
        {
            UserId = "user", PlanId = SubscriptionPlan.ProId, Status = "active", StartedAt = now
        });
        await ctx.Db.SaveChangesAsync();

        (await quota.GetStatusAsync("user", false)).Limit.Should().Be(5000);
    }

    [Fact]
    public void PlanFeatures_RoundTrip_BetweenListAndJson()
    {
        var plan = new SubscriptionPlan();

        // Editing as a list, then serializing, trims entries and drops blanks.
        plan.SetFeatures(new[] { "  Priority support  ", "", "Unlimited exports", "   " });
        plan.FeaturesJson.Should().Be("[\"Priority support\",\"Unlimited exports\"]");

        // Decoding back yields exactly the clean list the admin sees.
        plan.GetFeatures().Should().Equal("Priority support", "Unlimited exports");
    }

    [Fact]
    public void PlanFeatures_DecodesSeededJson_AndToleratesBadJson()
    {
        new SubscriptionPlan { FeaturesJson = "[\"a\",\"b\",\"c\"]" }.GetFeatures()
            .Should().Equal("a", "b", "c");

        // Null/blank and malformed JSON degrade to an empty list rather than throwing in the UI.
        new SubscriptionPlan { FeaturesJson = "" }.GetFeatures().Should().BeEmpty();
        new SubscriptionPlan { FeaturesJson = "not json" }.GetFeatures().Should().BeEmpty();
    }

    [Fact]
    public async Task SystemConfig_SeedsDefaults_AndRoundTripsTypedValues()
    {
        using var ctx = new PostgresTestContext();
        var cfg = new SystemConfigService(ctx.Db);

        await cfg.SeedDefaultsAsync();
        (await cfg.GetIntAsync("ratelimit.search_per_hour", -1)).Should().Be(5);
        (await cfg.GetBoolAsync("feature.export_enabled", false)).Should().BeTrue();

        await cfg.SetAsync("ratelimit.search_per_hour", "20", "int");
        (await cfg.GetIntAsync("ratelimit.search_per_hour", -1)).Should().Be(20);

        // Idempotent: re-seeding doesn't clobber an admin override.
        await cfg.SeedDefaultsAsync();
        (await cfg.GetIntAsync("ratelimit.search_per_hour", -1)).Should().Be(20);
    }

    [Fact]
    public async Task SeedDefaults_UpgradesRowsStillHoldingSupersededModelDefaults()
    {
        using var ctx = new PostgresTestContext();
        var cfg = new SystemConfigService(ctx.Db);
        await cfg.SeedDefaultsAsync();

        // Simulate a pre-Kimi database: rows exist with the OLD default models (never touched by an
        // operator) plus one genuine operator override that must survive the upgrade.
        await cfg.SetAsync("model.planner", "anthropic/claude-sonnet-5", "string");
        await cfg.SetAsync("model.default_pro", "anthropic/claude-sonnet-4", "string");
        await cfg.SetAsync("model.default_free", "openai/gpt-4o-mini", "string");
        await cfg.SetAsync("model.analyst", "google/gemini-2.5-pro", "string"); // deliberate override

        await cfg.SeedDefaultsAsync();

        // Rows still holding a superseded DEFAULT migrate to the current default (this is how prod
        // picks up the model switch without an admin-settings pass)…
        (await cfg.GetAsync("model.planner")).Should().Be(Daleel.Core.Llm.LlmCallSites.DefaultModel);
        (await cfg.GetAsync("model.default_pro")).Should().Be(Daleel.Core.Llm.LlmCallSites.DefaultModel);
        (await cfg.GetAsync("model.default_free")).Should().Be(Daleel.Core.Llm.LlmCallSites.DefaultModel);
        // …while a value an operator chose on purpose is never clobbered.
        (await cfg.GetAsync("model.analyst")).Should().Be("google/gemini-2.5-pro");
    }
}
