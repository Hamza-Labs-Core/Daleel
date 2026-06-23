using System.Security.Claims;
using Daleel.Web.Data;

namespace Daleel.Web.Conversation;

/// <summary>HTTP API for the async search flow. Rate-limited by IpRateLimitMiddleware (/api/search).</summary>
/// <remarks>
/// Antiforgery is disabled on these endpoints because they are JSON APIs, not browser form posts.
/// They are defended against CSRF by two layers: (1) the Identity auth cookie is
/// <c>SameSite=Lax</c>, so a cross-site POST omits the cookie and the request is rejected as
/// unauthenticated; and (2) the required <c>application/json</c> content-type forces a CORS preflight
/// for any cross-origin caller. The whole HTTP API is additionally gated behind the
/// <c>feature.api_access_enabled</c> system flag (off by default).
/// </remarks>
public static class ConversationEndpoints
{
    public sealed record SearchRequest(string Query, string? Geo, string? Model);
    public sealed record CancelRequest(int JobId);

    /// <summary>403 unless an admin has enabled <c>feature.api_access_enabled</c>.</summary>
    private static async Task<bool> ApiEnabledAsync(HttpContext http)
    {
        var config = http.RequestServices.GetService<ISystemConfigService>();
        return config is null || await config.GetBoolAsync("feature.api_access_enabled", false);
    }

    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        // Submit a search → 202 Accepted with the job id (the agent runs in the background worker).
        app.MapPost("/api/search", async (
            SearchRequest req,
            HttpContext http,
            IConversationService conversations) =>
        {
            if (!await ApiEnabledAsync(http))
            {
                return Results.Json(new { error = "API access is disabled." }, statusCode: StatusCodes.Status403Forbidden);
            }

            var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var isAdmin = http.User.IsInRole("Admin");
            var result = await conversations.SubmitAsync(userId, isAdmin, req.Query, req.Geo, req.Model);

            return result.Accepted
                ? Results.Accepted($"/api/search/{result.JobId}", new { jobId = result.JobId })
                : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
        }).RequireAuthorization().DisableAntiforgery();

        // Cancel a running/queued search.
        app.MapPost("/api/search/cancel", async (
            CancelRequest req,
            HttpContext http,
            IConversationService conversations) =>
        {
            if (!await ApiEnabledAsync(http))
            {
                return Results.Json(new { error = "API access is disabled." }, statusCode: StatusCodes.Status403Forbidden);
            }

            var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var ok = await conversations.CancelAsync(userId, req.JobId);
            return ok ? Results.Ok() : Results.NotFound();
        }).RequireAuthorization().DisableAntiforgery();

        return app;
    }
}
