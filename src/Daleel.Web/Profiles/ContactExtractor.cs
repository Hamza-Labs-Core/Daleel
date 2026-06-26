using System.Text.RegularExpressions;

namespace Daleel.Web.Profiles;

/// <summary>
/// Pulls the first plausible e-mail / phone out of scraped page text — a best-effort fallback for
/// when neither the LLM synthesis nor Google Places surfaced contact details. Deliberately
/// conservative: it would rather miss a contact than attach a wrong one to a store profile.
/// </summary>
public static partial class ContactExtractor
{
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    // A leading + or 0, then 7–14 more digits, allowing spaces, dashes and parentheses between them.
    [GeneratedRegex(@"(?:\+|00|0)[\d][\d\s\-()]{6,18}\d", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    /// <summary>First syntactically-valid e-mail in the text, or null.</summary>
    public static string? FirstEmail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = EmailRegex().Match(text);
        // Skip obvious asset/example addresses that are never real contacts.
        return m.Success && !m.Value.EndsWith("example.com", StringComparison.OrdinalIgnoreCase)
            ? m.Value
            : null;
    }

    /// <summary>First plausible phone number in the text (7–15 digits), or null.</summary>
    public static string? FirstPhone(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (Match m in PhoneRegex().Matches(text))
        {
            var digits = m.Value.Count(char.IsDigit);
            if (digits is >= 8 and <= 15)
            {
                return m.Value.Trim();
            }
        }

        return null;
    }
}
