using Microsoft.AspNetCore.Identity;

namespace Daleel.Web.Data;

/// <summary>
/// The application's user, persisted in the standard Identity <c>AspNetUsers</c> table.
/// Extends <see cref="IdentityUser"/> with a couple of profile fields populated from the
/// external provider (display name and avatar) so the UI can greet the visitor without an
/// extra round-trip to the provider.
/// </summary>
public sealed class ApplicationUser : IdentityUser
{
    /// <summary>Friendly name surfaced by the external provider (e.g. "Mahmoud Darwish").</summary>
    public string? DisplayName { get; set; }

    /// <summary>Avatar/picture URL surfaced by the external provider, if any.</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>True for users granted the admin role (aggregate stats only — never row data).</summary>
    public bool IsAdmin { get; set; }

    /// <summary>True when an admin has disabled the account (blocks sign-in).</summary>
    public bool IsDisabled { get; set; }

    /// <summary>UTC creation time, for "registered today/week/month" stats.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC time of the user's most recent sign-in.</summary>
    public DateTime? LastActiveAt { get; set; }
}
