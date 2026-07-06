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
