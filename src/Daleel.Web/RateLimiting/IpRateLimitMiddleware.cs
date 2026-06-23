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

    /// <summary>Honors the proxy's X-Forwarded-For first hop, falling back to the socket address.</summary>
    private static string ClientIp(HttpContext context)
    {
        var fwd = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fwd))
        {
            return fwd.Split(',')[0].Trim();
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
