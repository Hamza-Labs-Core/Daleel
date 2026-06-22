using Microsoft.Extensions.Caching.Memory;

namespace Daleel.Web.RateLimiting;

/// <summary>A named fixed-window rate rule: at most <see cref="Limit"/> hits per <see cref="Window"/>.</summary>
public sealed record RateRule(string Name, int Limit, TimeSpan Window);

/// <summary>In-memory, per-IP fixed-window rate limiter (no Redis needed at this scale).</summary>
public interface IIpRateLimiter
{
    /// <summary>
    /// Records a hit for <paramref name="ipKey"/> against <paramref name="rule"/>. Returns false when
    /// the window's limit is exceeded, with <paramref name="retryAfterSeconds"/> until it resets.
    /// </summary>
    bool IsAllowed(string ipKey, RateRule rule, out int retryAfterSeconds);
}

public sealed class IpRateLimiter : IIpRateLimiter
{
    private readonly IMemoryCache _cache;
    private readonly Func<DateTimeOffset> _clock;

    public IpRateLimiter(IMemoryCache cache, Func<DateTimeOffset>? clock = null)
    {
        _cache = cache;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool IsAllowed(string ipKey, RateRule rule, out int retryAfterSeconds)
    {
        var now = _clock();
        // Align to a fixed window so every IP shares the same boundary and the counter self-expires.
        var windowTicks = rule.Window.Ticks;
        var windowStart = new DateTimeOffset(now.Ticks - (now.Ticks % windowTicks), TimeSpan.Zero);
        var windowEnd = windowStart.Add(rule.Window);
        var key = $"rl:{rule.Name}:{ipKey}:{windowStart.Ticks}";

        var counter = _cache.GetOrCreate(key, entry =>
        {
            // Expire relative to real wall-clock time (MemoryCache uses the system clock, not our
            // injected one) — the bucketed key already segments counting by the logical window.
            entry.AbsoluteExpirationRelativeToNow = rule.Window;
            return new Counter();
        })!;

        var count = counter.Increment();
        if (count > rule.Limit)
        {
            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((windowEnd - now).TotalSeconds));
            return false;
        }

        retryAfterSeconds = 0;
        return true;
    }

    /// <summary>Thread-safe per-window hit counter.</summary>
    private sealed class Counter
    {
        private int _count;
        public int Increment() => Interlocked.Increment(ref _count);
    }
}
