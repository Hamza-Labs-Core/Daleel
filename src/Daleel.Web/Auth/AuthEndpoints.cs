using Daleel.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace Daleel.Web.Auth;

/// <summary>
/// HTTP endpoints that must run in a real request context (not a Blazor circuit). Sign-in and
/// registration live on the static-SSR <c>/login</c> and <c>/register</c> pages — they post back to
/// themselves and call <see cref="SignInManager{T}"/> during that POST. Logout stays here as a plain
/// endpoint so it can be a CSRF-safe POST target.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Clear the auth cookie. POST-only so a third-party page can't force a logout with an <img>/GET
        // (a state change must not be triggerable by a simple cross-site navigation). The /logout page
        // renders the submitting form with an antiforgery token.
        app.MapPost("/auth/logout", async (SignInManager<ApplicationUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.LocalRedirect("/");
        });

        return app;
    }
}
