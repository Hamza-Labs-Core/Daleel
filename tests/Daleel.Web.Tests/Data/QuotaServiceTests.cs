using Daleel.Web.Data;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Data;

public class QuotaServiceTests
{
    private const string User = "quota-user";

    private static QuotaService Make(SqliteTestContext ctx, DateTimeOffset now) =>
        new(ctx.Db, () => now);

    [Fact]
    public async Task BasicUser_GetsFiveSearches_SixthRejected()
    {
        using var ctx = new SqliteTestContext();
        var svc = Make(ctx, new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));

        var status = await svc.GetStatusAsync(User, isAdmin: false);
        status.PlanName.Should().Be("Basic");
        status.Limit.Should().Be(5);

        for (var i = 0; i < 5; i++)
        {
            (await svc.TryConsumeAsync(User, false)).Should().BeTrue($"search {i + 1} is within quota");
        }

        (await svc.TryConsumeAsync(User, false)).Should().BeFalse("the 6th search exceeds the free quota");
        (await svc.GetStatusAsync(User, false)).Remaining.Should().Be(0);
    }

    [Fact]
    public async Task ProSubscriber_GetsHundredSearches()
    {
        using var ctx = new SqliteTestContext();
        ctx.Db.UserSubscriptions.Add(new UserSubscription
        {
            UserId = User, PlanId = SubscriptionPlan.ProId, Status = "active",
            StartedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)
        });
        await ctx.Db.SaveChangesAsync();

        var svc = Make(ctx, new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));
        var status = await svc.GetStatusAsync(User, false);

        status.PlanName.Should().Be("Pro");
        status.Limit.Should().Be(100);
        status.CanSearch.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_BypassesQuota()
    {
        using var ctx = new SqliteTestContext();
        var svc = Make(ctx, new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));

        // Far beyond Basic's 5 — admin is never blocked.
        for (var i = 0; i < 10; i++)
        {
            (await svc.TryConsumeAsync(User, isAdmin: true)).Should().BeTrue();
        }

        (await svc.GetStatusAsync(User, true)).CanSearch.Should().BeTrue();
    }

    [Fact]
    public async Task Quota_ResetsOnNewMonth()
    {
        using var ctx = new SqliteTestContext();
        var june = new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);
        var juneSvc = Make(ctx, june);

        for (var i = 0; i < 5; i++)
        {
            await juneSvc.TryConsumeAsync(User, false);
        }
        (await juneSvc.TryConsumeAsync(User, false)).Should().BeFalse();

        // Roll into July — the counter resets and the user can search again.
        var july = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var julySvc = Make(ctx, july);

        var status = await julySvc.GetStatusAsync(User, false);
        status.Used.Should().Be(0);
        status.CanSearch.Should().BeTrue();
        (await julySvc.TryConsumeAsync(User, false)).Should().BeTrue();
    }
}
