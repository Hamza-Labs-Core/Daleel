using Daleel.Web.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Daleel.Web.Auth;

/// <summary>
/// HTTP endpoints for sign-in, registration and sign-out. These deliberately run as raw minimal-API
/// requests — never inside a Blazor circuit — because writing the Identity auth cookie appends a
/// <c>Set-Cookie</c> response header, which throws "Headers are read-only, response has already started"
/// if the response has begun streaming (as it does when a page renders InteractiveServer). The
/// <c>/login</c> and <c>/register</c> pages are plain static forms that POST here; this is the layer
/// where the cookie is actually written.
/// </summary>
/// <remarks>
/// Antiforgery: every endpoint binds form fields via <see cref="FromFormAttribute"/>, so the
/// <c>UseAntiforgery()</c> middleware automatically requires a valid token — supplied by the
/// <c>&lt;AntiforgeryToken /&gt;</c> rendered inside each static form.
/// </remarks>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Sign in ────────────────────────────────────────────────────────────
        app.MapPost("/auth/login", async (
            [FromForm] string? email,
            [FromForm] string? password,
            [FromForm] string? returnUrl,
            HttpContext http,
            IAntiforgery antiforgery,
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            IAnalyticsService analytics) =>
        {
            var safeReturn = SafeLocalPath(returnUrl);

            // Validate antiforgery ourselves (the endpoint opts out of the automatic check via
            // DisableAntiforgery) so a failure becomes a friendly "your session expired, try again"
            // instead of a blank HTTP 400 — the page the mobile/Safari report saw.
            if (!await IsAntiforgeryValidAsync(antiforgery, http))
            {
                return RedirectWithError("/login", "expired", safeReturn);
            }

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                return RedirectWithError("/login", "invalid", safeReturn);
            }

            var user = await userManager.FindByEmailAsync(email);

            // Identical "invalid" response whether the email is unknown or the password is wrong — never
            // reveal which accounts exist.
            if (user is null)
            {
                return RedirectWithError("/login", "invalid", safeReturn);
            }

            if (user.IsDisabled)
            {
                return RedirectWithError("/login", "disabled", safeReturn);
            }

            var result = await signInManager.PasswordSignInAsync(
                user.UserName!, password, isPersistent: true, lockoutOnFailure: false);

            if (result.Succeeded)
            {
                user.LastActiveAt = DateTime.UtcNow;
                await userManager.UpdateAsync(user);
                await analytics.RecordLoginAsync(user.Id, "Password", http.Connection.RemoteIpAddress?.ToString());
                // The 302 carries the freshly-written auth cookie back to the browser.
                return Results.LocalRedirect(safeReturn);
            }

            return RedirectWithError("/login", result.IsLockedOut ? "lockedout" : "invalid", safeReturn);
        }).DisableAntiforgery();

        // ── Register ───────────────────────────────────────────────────────────
        app.MapPost("/auth/register", async (
            [FromForm] string? email,
            [FromForm] string? password,
            [FromForm] string? confirmPassword,
            [FromForm] string? returnUrl,
            HttpContext http,
            IAntiforgery antiforgery,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            IAnalyticsService analytics,
            IConfiguration config) =>
        {
            var safeReturn = SafeLocalPath(returnUrl);

            if (!await IsAntiforgeryValidAsync(antiforgery, http))
            {
                return RedirectWithError("/register", "expired", safeReturn);
            }

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                return RedirectWithError("/register", "missing", safeReturn);
            }

            if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
            {
                return RedirectWithError("/register", "mismatch", safeReturn);
            }

            // Admin bootstrap. The secure path is an explicit DALEEL_ADMIN_EMAILS allowlist
            // (comma-separated): only those addresses become admin, and the implicit "first user wins"
            // promotion is switched off — so on a fresh internet-facing deploy a random first registrant
            // cannot seize the instance. With no allowlist configured we keep the single-tenant
            // convenience: the very first account to register is promoted (checked before creation so a
            // race can't mint two admins; AnyAsync short-circuits on the first row).
            var adminEmails = (config["DALEEL_ADMIN_EMAILS"] ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var isAdmin = adminEmails.Length > 0
                ? adminEmails.Any(e => string.Equals(e, email, StringComparison.OrdinalIgnoreCase))
                : !await userManager.Users.AsNoTracking().AnyAsync();

            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = email.Split('@')[0],
                CreatedAt = DateTime.UtcNow,
                LastActiveAt = DateTime.UtcNow,
                IsAdmin = isAdmin,
            };

            var result = await userManager.CreateAsync(user, password);
            if (!result.Succeeded)
            {
                // Map Identity's error codes to a small set of friendly query codes the page renders.
                var code = result.Errors.Any(e => e.Code.Contains("Duplicate", StringComparison.OrdinalIgnoreCase))
                    ? "duplicate"
                    : result.Errors.Any(e => e.Code.Contains("Password", StringComparison.OrdinalIgnoreCase))
                        ? "weakpassword"
                        : "failed";
                return RedirectWithError("/register", code, safeReturn);
            }

            await signInManager.SignInAsync(user, isPersistent: true);
            await analytics.RecordLoginAsync(user.Id, "Password", http.Connection.RemoteIpAddress?.ToString());
            return Results.LocalRedirect(safeReturn);
        }).DisableAntiforgery();

        // ── Sign out ───────────────────────────────────────────────────────────
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

    /// <summary>
    /// Validates the antiforgery token, returning false instead of throwing so the caller can redirect
    /// to a friendly "session expired" page rather than surface a blank HTTP 400.
    /// </summary>
    private static async Task<bool> IsAntiforgeryValidAsync(IAntiforgery antiforgery, HttpContext http)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(http);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Coerces a return URL to a safe local path, rejecting open-redirect forms (absolute URLs,
    /// protocol-relative <c>//host</c>, and back-slash tricks). Public for unit testing.
    /// </summary>
    public static string SafeLocalPath(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : "/";

    /// <summary>
    /// Builds a local redirect back to an auth page carrying an <c>error</c> code (and the preserved
    /// return URL, when it isn't the default "/"). The page maps the code to a localized message.
    /// </summary>
    private static IResult RedirectWithError(string page, string code, string safeReturn)
    {
        var target = $"{page}?error={code}";
        if (safeReturn != "/")
        {
            target += $"&returnUrl={Uri.EscapeDataString(safeReturn)}";
        }

        return Results.LocalRedirect(target);
    }
}
