using System.Security.Cryptography;
using System.Text;

namespace Daleel.Web.Data;

/// <summary>
/// One-way pseudonymisation for analytics/cost logging. Search analytics, page views, and API
/// cost rows store a deterministic hash of the user id instead of the id itself, so the data can
/// be aggregated and a user can still see their own rows, but an admin browsing the tables can't
/// trace a row back to a specific account.
/// </summary>
/// <remarks>
/// Quota state (<c>UserQuota</c>) and the user's own search history keep the raw id — those are
/// operational/personal, not analytics. Only analytics/cost surfaces are anonymised.
/// </remarks>
public static class Anonymizer
{
    /// <summary>SHA-256 (truncated to 64 bits) of a user id, or null when there's no id.</summary>
    public static string? HashUserId(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(userId));
        return Convert.ToHexString(bytes)[..16];
    }
}
