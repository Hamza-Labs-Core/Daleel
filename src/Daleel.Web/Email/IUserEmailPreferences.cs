using Daleel.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace Daleel.Web.Email;

/// <summary>A user's email address plus whether they've opted in to search-result emails.</summary>
public sealed record EmailRecipient(string Email, string? DisplayName, bool WantsSearchEmails);

/// <summary>
/// Resolves a user id to their email + notification preference. This is the one seam that touches
/// <see cref="UserManager{TUser}"/>, so the notifier above it stays trivial to unit-test.
/// </summary>
public interface IUserEmailPreferences
{
    /// <summary>Returns the recipient, or null when the user is unknown or has no email address.</summary>
    Task<EmailRecipient?> GetRecipientAsync(string userId, CancellationToken ct = default);
}

/// <summary>Reads the recipient from the Identity store (<see cref="ApplicationUser"/>).</summary>
public sealed class UserEmailPreferences : IUserEmailPreferences
{
    private readonly UserManager<ApplicationUser> _users;

    public UserEmailPreferences(UserManager<ApplicationUser> users) => _users = users;

    public async Task<EmailRecipient?> GetRecipientAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var user = await _users.FindByIdAsync(userId).ConfigureAwait(false);
        if (user is null || string.IsNullOrWhiteSpace(user.Email))
        {
            return null;
        }

        return new EmailRecipient(user.Email, user.DisplayName, user.EmailSearchResults);
    }
}
