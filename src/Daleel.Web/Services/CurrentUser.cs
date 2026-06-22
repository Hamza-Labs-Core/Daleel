using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Daleel.Web.Services;

/// <summary>The authenticated visitor, as seen from inside the Blazor circuit.</summary>
public interface ICurrentUser
{
    /// <summary>The Identity user id (AspNetUsers.Id), or null if not signed in.</summary>
    Task<string?> GetUserIdAsync();

    /// <summary>True when a user is signed in.</summary>
    Task<bool> IsAuthenticatedAsync();

    /// <summary>Display name for the UI (falls back to the name claim, then email).</summary>
    Task<string?> GetDisplayNameAsync();

    /// <summary>True when the signed-in user is in the Admin role.</summary>
    Task<bool> IsAdminAsync();
}

/// <summary>
/// Resolves the current user from the circuit's <see cref="AuthenticationStateProvider"/> rather
/// than from <c>HttpContext</c>. In Blazor Server there is no live <c>HttpContext</c> after the
/// initial handshake — identity flows to interactive components through the cascading
/// <see cref="AuthenticationState"/>, which this provider exposes.
/// </summary>
public sealed class CurrentUser : ICurrentUser
{
    private readonly AuthenticationStateProvider _authState;

    public CurrentUser(AuthenticationStateProvider authState) => _authState = authState;

    public async Task<string?> GetUserIdAsync()
    {
        var principal = await PrincipalAsync();
        return principal.Identity?.IsAuthenticated == true
            ? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            : null;
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        var principal = await PrincipalAsync();
        return principal.Identity?.IsAuthenticated == true;
    }

    public async Task<string?> GetDisplayNameAsync()
    {
        var principal = await PrincipalAsync();
        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return principal.FindFirst("display_name")?.Value
            ?? principal.FindFirst(ClaimTypes.Name)?.Value
            ?? principal.Identity.Name
            ?? principal.FindFirst(ClaimTypes.Email)?.Value;
    }

    public async Task<bool> IsAdminAsync()
    {
        var principal = await PrincipalAsync();
        return principal.IsInRole("Admin");
    }

    private async Task<ClaimsPrincipal> PrincipalAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        return state.User;
    }
}
