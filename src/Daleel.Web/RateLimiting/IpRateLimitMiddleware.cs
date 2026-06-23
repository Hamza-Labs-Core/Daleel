namespace Daleel.Web.RateLimiting;

/// <summary>
/// IP-based rate limiting, applied first in the pipeline so abusive clients are rejected before any
/// auth/quota work. Three fixed-window rules by path:
/// <list type="bullet">
///   <item>/api/search — 5 per hour (search abuse guard)</item>
///   <item>/api/* — 10 per minute</item>
///   <item>everything else (page loads) — 100 per minute</item>
/// </list>
/// Static assets, the Blazor circuit, and the health probe are exempt.
/// </summary>
public sealed class IpRateLimitMiddleware
{
    private static readonly RateRule PageRule = new("page", 100, TimeSpan.FromMinutes(1));
    private static readonly RateRule ApiRule = new("api", 10, TimeSpan.FromMinutes(1));
    private static readonly RateRule SearchRule = new("search", 5, TimeSpan.FromHours(1));

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

        foreach (var rule in RulesFor(path))
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

    private static IEnumerable<RateRule> RulesFor(string path)
    {
        if (path.StartsWith("/api/search", StringComparison.OrdinalIgnoreCase))
        {
            yield return SearchRule;
            yield return ApiRule;
        }
        else if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            yield return ApiRule;
        }
        else
        {
            yield return PageRule;
        }
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
