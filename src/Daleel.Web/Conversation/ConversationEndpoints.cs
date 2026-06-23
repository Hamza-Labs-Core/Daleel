using System.Security.Claims;

namespace Daleel.Web.Conversation;

/// <summary>HTTP API for the async search flow. Rate-limited by IpRateLimitMiddleware (/api/search).</summary>
public static class ConversationEndpoints
{
    public sealed record SearchRequest(string Query, string? Geo, string? Model);
    public sealed record CancelRequest(int JobId);

    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        // Submit a search → 202 Accepted with the job id (the agent runs in the background worker).
        app.MapPost("/api/search", async (
            SearchRequest req,
            HttpContext http,
            IConversationService conversations) =>
        {
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
