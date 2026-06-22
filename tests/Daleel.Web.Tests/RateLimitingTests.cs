using Daleel.Web.RateLimiting;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Daleel.Web.Tests;

public class RateLimitingTests
{
    [Fact]
    public void Allows_UpToLimit_ThenBlocks()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var now = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var limiter = new IpRateLimiter(cache, () => now);
        var rule = new RateRule("test", Limit: 3, TimeSpan.FromMinutes(1));

        limiter.IsAllowed("1.2.3.4", rule, out _).Should().BeTrue();
        limiter.IsAllowed("1.2.3.4", rule, out _).Should().BeTrue();
        limiter.IsAllowed("1.2.3.4", rule, out _).Should().BeTrue();

        limiter.IsAllowed("1.2.3.4", rule, out var retryAfter).Should().BeFalse("the 4th hit exceeds the limit");
        retryAfter.Should().BeGreaterThan(0);
    }

    [Fact]
    public void IsolatesPerIp()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var now = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var limiter = new IpRateLimiter(cache, () => now);
        var rule = new RateRule("test", Limit: 1, TimeSpan.FromMinutes(1));

        limiter.IsAllowed("a", rule, out _).Should().BeTrue();
        limiter.IsAllowed("a", rule, out _).Should().BeFalse();
        // A different IP has its own bucket.
        limiter.IsAllowed("b", rule, out _).Should().BeTrue();
    }

    [Fact]
    public void Resets_AfterWindowElapses()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var current = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var limiter = new IpRateLimiter(cache, () => current);
        var rule = new RateRule("test", Limit: 1, TimeSpan.FromMinutes(1));

        limiter.IsAllowed("ip", rule, out _).Should().BeTrue();
        limiter.IsAllowed("ip", rule, out _).Should().BeFalse();

        // Advance past the window → a fresh bucket allows again.
        current = current.AddMinutes(2);
        limiter.IsAllowed("ip", rule, out _).Should().BeTrue();
    }
}
