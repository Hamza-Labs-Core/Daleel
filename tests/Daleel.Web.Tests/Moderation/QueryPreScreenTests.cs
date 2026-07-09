using Daleel.Core.Moderation;
using Daleel.Web.Moderation;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Moderation;

/// <summary>
/// The zero-cost haram-consumable query gate: alcohol/pork/drugs requests are blocked at submission
/// (no SearchJob, no spend), while everything else — including riba/financial queries — passes. Fail-open.
/// </summary>
public class QueryPreScreenTests
{
    private static QueryPreScreen Screen() => new(new StubPolicy());

    [Theory]
    [InlineData("beer")]
    [InlineData("buy wine in amman")]
    [InlineData("best whiskey prices")]
    [InlineData("pork chops")]
    [InlineData("bacon delivery")]
    public async Task Haram_consumable_queries_are_blocked(string query)
    {
        var result = await Screen().ScreenAsync(query);
        result.Blocked.Should().BeTrue($"'{query}' is a haram consumable and must never be searched");
        result.Category.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("best loans")]          // riba — steered to Islamic elsewhere, NOT blocked here
    [InlineData("credit cards")]
    [InlineData("barber near me")]      // contains "bar" — must not false-block
    [InlineData("hamlet book")]         // contains "ham" — must not false-block
    [InlineData("espresso machine jordan")]
    public async Task Non_haram_queries_pass(string query) =>
        (await Screen().ScreenAsync(query)).Blocked.Should().BeFalse($"'{query}' is not a haram consumable");

    [Theory]
    [InlineData("best loans")]
    [InlineData("credit cards jordan")]
    [InlineData("home mortgage rates")]
    [InlineData("قرض شخصي")]
    public async Task Riba_product_queries_are_steered_to_islamic(string query)
    {
        var result = await Screen().ScreenAsync(query);
        result.Blocked.Should().BeFalse("a financial product is steered, never blocked");
        result.SteeredQuery.Should().NotBeNull();
        result.SteeredQuery!.Should().Contain("islamic").And.Contain(query);
    }

    [Fact]
    public async Task Already_islamic_finance_query_is_not_double_steered() =>
        (await Screen().ScreenAsync("islamic personal loans")).SteeredQuery.Should().BeNull();

    [Fact]
    public async Task A_normal_product_is_not_steered() =>
        // "laptop" is not a financial product — no steer even though stores may offer installments.
        (await Screen().ScreenAsync("best laptop deals")).SteeredQuery.Should().BeNull();

    [Fact]
    public async Task Empty_query_fails_open() =>
        (await Screen().ScreenAsync("   ")).Blocked.Should().BeFalse();

    [Fact]
    public async Task A_policy_load_error_fails_open() =>
        (await new QueryPreScreen(new ThrowingPolicy()).ScreenAsync("beer")).Blocked.Should()
            .BeFalse("moderation must never hard-fail a submission");

    private sealed class StubPolicy : IModerationPolicyProvider
    {
        public Task<ModerationPolicySnapshot> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new ModerationPolicySnapshot(
                Array.Empty<string>(), new HalalPolicy(),
                ContentFilter.BuildCategories(Array.Empty<ModerationRule>())));
    }

    private sealed class ThrowingPolicy : IModerationPolicyProvider
    {
        public Task<ModerationPolicySnapshot> GetAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("policy unavailable");
    }
}
