using System.Security.Claims;
using Daleel.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace Daleel.Web.Auth;

/// <summary>
/// The three HTTP endpoints behind external login. These run in a real request context (not a
/// Blazor circuit), so they can use <see cref="SignInManager{T}"/> and issue redirects directly.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Kick off the OAuth dance: challenge the chosen provider, asking it to come back to /auth/callback.
        app.MapGet("/auth/signin/{provider}", async (
            string provider,
            string? returnUrl,
            SignInManager<ApplicationUser> signInManager,
            IAuthenticationSchemeProvider schemes) =>
        {
            // Guard against a hand-typed URL for an unconfigured provider (which would otherwise 500).
            if (await schemes.GetSchemeAsync(provider) is null)
            {
                return Results.Redirect("/login?error=external");
            }

            var target = Local(returnUrl);
            var redirectUrl = $"/auth/callback?returnUrl={Uri.EscapeDataString(target)}";
            var props = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
            return Results.Challenge(props, new[] { provider });
        });

        // Provider returns here. Sign in an existing login, or auto-provision a user on first visit.
        app.MapGet("/auth/callback", async (
            string? returnUrl,
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager) =>
        {
            var target = Local(returnUrl);

            var info = await signInManager.GetExternalLoginInfoAsync();
            if (info is null)
            {
                return Results.Redirect("/login?error=external");
            }

            var signin = await signInManager.ExternalLoginSignInAsync(
                info.LoginProvider, info.ProviderKey, isPersistent: true, bypassTwoFactor: true);
            if (signin.Succeeded)
            {
                return Results.LocalRedirect(target);
            }

            if (signin.IsLockedOut)
            {
                return Results.Redirect("/login?error=lockedout");
            }

            // First time this external identity is seen — create the local user and link the login.
            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            var user = new ApplicationUser
            {
                UserName = email ?? $"{info.LoginProvider.ToLowerInvariant()}:{info.ProviderKey}",
                Email = email,
                EmailConfirmed = email is not null,
                DisplayName = info.Principal.FindFirstValue(ClaimTypes.Name),
                AvatarUrl = Picture(info.Principal)
            };

            var created = await userManager.CreateAsync(user);
            if (!created.Succeeded)
            {
                return Results.Redirect("/login?error=create");
            }

            await userManager.AddLoginAsync(user, info);
            await signInManager.SignInAsync(user, isPersistent: true);
            return Results.LocalRedirect(target);
        });

        // Clear the auth cookie. GET keeps the nav-bar link trivial; signing out is not a sensitive
        // state change, and the cookie is HttpOnly + SameSite so this can't leak anything.
        app.MapGet("/auth/logout", async (SignInManager<ApplicationUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.LocalRedirect("/");
        });

        return app;
    }

    /// <summary>Coerces a return URL to a safe local path, defaulting to the home page.</summary>
    private static string Local(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/') ? returnUrl : "/";

    /// <summary>Best-effort avatar URL across the various provider claim conventions.</summary>
    private static string? Picture(ClaimsPrincipal principal)
    {
        foreach (var claim in new[] { "picture", "urn:google:picture", "image", "avatar_url", "urn:github:avatar" })
        {
            var value = principal.FindFirstValue(claim);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
