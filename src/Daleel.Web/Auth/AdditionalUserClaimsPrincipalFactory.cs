using System.Security.Claims;
using Daleel.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Daleel.Web.Auth;

/// <summary>
/// Enriches the signed-in principal with <c>display_name</c> and <c>avatar_url</c> claims drawn
/// from the user's profile, so the UI can render the visitor's name and avatar straight from the
/// auth cookie without hitting the database on every render.
/// </summary>
public sealed class AdditionalUserClaimsPrincipalFactory
    : UserClaimsPrincipalFactory<ApplicationUser>
{
    public AdditionalUserClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        IOptions<IdentityOptions> options)
        : base(userManager, options)
    {
    }

    public override async Task<ClaimsPrincipal> CreateAsync(ApplicationUser user)
    {
        var principal = await base.CreateAsync(user);
        var identity = (ClaimsIdentity)principal.Identity!;

        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            identity.AddClaim(new Claim("display_name", user.DisplayName));
        }

        if (!string.IsNullOrWhiteSpace(user.AvatarUrl))
        {
            identity.AddClaim(new Claim("avatar_url", user.AvatarUrl));
        }

        if (user.IsAdmin)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
        }

        return principal;
    }
}
