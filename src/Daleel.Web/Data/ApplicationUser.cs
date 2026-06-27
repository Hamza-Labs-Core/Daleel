using Microsoft.AspNetCore.Identity;

namespace Daleel.Web.Data;

/// <summary>
/// The application's user, persisted in the standard Identity <c>AspNetUsers</c> table.
/// Extends <see cref="IdentityUser"/> with a couple of profile fields (display name and avatar)
/// so the UI can greet the visitor without an extra round-trip.
/// </summary>
public sealed class ApplicationUser : IdentityUser
{
    /// <summary>Friendly display name (defaults to the local part of the email at registration).</summary>
    public string? DisplayName { get; set; }

    /// <summary>Optional avatar/picture URL for the user.</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>True for users granted the admin role (aggregate stats only — never row data).</summary>
    public bool IsAdmin { get; set; }

    /// <summary>True when an admin has disabled the account (blocks sign-in).</summary>
    public bool IsDisabled { get; set; }

    /// <summary>UTC creation time, for "registered today/week/month" stats.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC time of the user's most recent sign-in.</summary>
    public DateTime? LastActiveAt { get; set; }

    /// <summary>
    /// Whether the user wants an email when one of their searches finishes. Defaults to true (opt-out):
    /// the toggle lives in account settings. Read by the background search worker on completion, so it
    /// must be server-side state — a browser/localStorage preference would be invisible to the worker.
    /// </summary>
    public bool EmailSearchResults { get; set; } = true;
}
