using Daleel.Web.Data;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Data;

public class QuotaServiceTests
{
    private const string User = "quota-user";

    private static QuotaService Make(PostgresTestContext ctx, DateTimeOffset now) =>
        new(ctx.Db, () => now);

    [Fact]
    public async Task BasicUser_GetsMonthlyCredits_BlockedWhenExhausted()
    {
        using var ctx = new PostgresTestContext();
        var svc = Make(ctx, new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));

        var status = await svc.GetStatusAsync(User, isAdmin: false);
        status.PlanName.Should().Be("Basic");
        status.Limit.Should().Be(500);
        status.CanSearch.Should().BeTrue();

        // Spend most of the allowance — still has room.
        await svc.ChargeCreditsAsync(User, 450);
        var mid = await svc.GetStatusAsync(User, false);
        mid.Used.Should().Be(450);
        mid.Remaining.Should().Be(50);
        mid.CanSearch.Should().BeTrue();

        // Spend the rest — now blocked.
        await svc.ChargeCreditsAsync(User, 60);
        var done = await svc.GetStatusAsync(User, false);
        done.Used.Should().Be(510);
        done.Remaining.Should().Be(0);
        done.CanSearch.Should().BeFalse("the period's credits are exhausted");
    }

    [Fact]
    public async Task ProSubscriber_GetsFiveThousandCredits()
    {
        using var ctx = new PostgresTestContext();
        ctx.Db.UserSubscriptions.Add(new UserSubscription
        {
            UserId = User, PlanId = SubscriptionPlan.ProId, Status = "active",
            StartedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)
        });
        await ctx.Db.SaveChangesAsync();

        var svc = Make(ctx, new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));
        var status = await svc.GetStatusAsync(User, false);

        status.PlanName.Should().Be("Pro");
        status.Limit.Should().Be(5000);
        status.CanSearch.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_BypassesCreditLimit()
    {
        using var ctx = new PostgresTestContext();
        var svc = Make(ctx, new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));

        // Spend far beyond Basic's 500 — admin is never blocked.
        await svc.ChargeCreditsAsync(User, 100_000);

        var status = await svc.GetStatusAsync(User, isAdmin: true);
        status.CanSearch.Should().BeTrue();
        status.Remaining.Should().BeNull("admins aren't credit-limited");
    }

    [Fact]
    public async Task Credits_ResetOnNewMonth()
    {
        using var ctx = new PostgresTestContext();
        var june = new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);
        var juneSvc = Make(ctx, june);

        await juneSvc.ChargeCreditsAsync(User, 500);
        (await juneSvc.GetStatusAsync(User, false)).CanSearch.Should().BeFalse();

        // Roll into July — the counter resets and the user can search again.
        var july = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var julySvc = Make(ctx, july);

        var status = await julySvc.GetStatusAsync(User, false);
        status.Used.Should().Be(0);
        status.CanSearch.Should().BeTrue();
    }
}
