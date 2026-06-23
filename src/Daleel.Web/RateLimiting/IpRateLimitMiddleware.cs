using Daleel.Web.Data;

namespace Daleel.Web.RateLimiting;

/// <summary>
/// IP-based rate limiting, applied first in the pipeline so abusive clients are rejected before any
/// auth/quota work. Three fixed-window rules by path:
/// <list type="bullet">
///   <item>/api/search — N per hour (search abuse guard)</item>
///   <item>/api/* — N per minute</item>
///   <item>everything else (page loads) — N per minute</item>
/// </list>
/// The per-window limits are read live from <see cref="ISystemConfigService"/> (admin-editable at
/// /admin/settings), falling back to the defaults below. Static assets, the Blazor circuit, and the
/// health probe are exempt.
/// </summary>
public sealed class IpRateLimitMiddleware
{
    private const int DefaultPageLimit = 100;
    private const int DefaultApiLimit = 10;
    private const int DefaultSearchLimit = 5;

    private readonly RequestDelegate _next;
    private readonly IIpRateLimiter _limiter;

    public IpRateLimitMiddleware(RequestDelegate next, IIpRateLimiter limiter)
    {
        _next = next;
        _limiter = limiter;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (IsExempt(path))
        {
            await _next(context);
            return;
        }

        var ip = ClientIp(context);

        foreach (var rule in await RulesForAsync(path, context))
        {
            if (!_limiter.IsAllowed(ip, rule, out var retryAfter))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = retryAfter.ToString();
                await context.Response.WriteAsync("Too many requests, please wait and try again.");
                return;
            }
        }

        await _next(context);
    }

    /// <summary>
    /// The rules that apply to <paramref name="path"/>, with limits resolved from the admin-editable
    /// system config (cached) and the constants above as fallback. The rule <em>names</em> stay fixed
    /// so the limiter's per-window buckets remain stable across a limit change.
    /// </summary>
    private static async Task<IReadOnlyList<RateRule>> RulesForAsync(string path, HttpContext context)
    {
        var config = context.RequestServices.GetService<ISystemConfigService>();

        async Task<int> Limit(string key, int fallback) =>
            config is null ? fallback : await config.GetIntAsync(key, fallback);

        if (path.StartsWith("/api/search", StringComparison.OrdinalIgnoreCase))
        {
            return new[]
            {
                new RateRule("search", await Limit("ratelimit.search_per_hour", DefaultSearchLimit), TimeSpan.FromHours(1)),
                new RateRule("api", await Limit("ratelimit.api_per_minute", DefaultApiLimit), TimeSpan.FromMinutes(1)),
            };
        }

        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            return new[]
            {
                new RateRule("api", await Limit("ratelimit.api_per_minute", DefaultApiLimit), TimeSpan.FromMinutes(1)),
            };
        }

        return new[]
        {
            new RateRule("page", await Limit("ratelimit.page_per_minute", DefaultPageLimit), TimeSpan.FromMinutes(1)),
        };
    }

    private static bool IsExempt(string path) =>
        path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/_content", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/app.css", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/daleel.js", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The resolved client address. We deliberately use <see cref="ConnectionInfo.RemoteIpAddress"/>
    /// — which <c>UseForwardedHeaders</c> (run earlier in the pipeline) has already set from the
    /// trusted proxy hop — rather than re-parsing the raw <c>X-Forwarded-For</c> header. Reading the
    /// header's first entry is spoofable: a client can send <c>X-Forwarded-For: 1.2.3.4</c> and the
    /// proxy appends the real address after it, so the first entry is attacker-controlled and would
    /// let a single client rotate it to evade every per-IP limit (including the 5/hour search guard).
    /// </summary>
    private static string ClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
