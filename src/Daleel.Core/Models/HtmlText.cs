using System.Net;
using System.Text.RegularExpressions;

namespace Daleel.Core.Models;

/// <summary>
/// Strips HTML that leaked into scraped product data (names, spec values, descriptions). Blazor
/// escapes by default, so leaked tags render as VISIBLE "&lt;span&gt;…" text — worse than either
/// rendering or removing them. One shared cleaner, applied at display edges so saved results are
/// covered as well as new ones.
/// </summary>
public static partial class HtmlText
{
    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.Compiled)]
    private static partial Regex Runs();

    /// <summary>Tag-free, entity-decoded, whitespace-collapsed text. Null/blank passes through.</summary>
    public static string? Strip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains('<') && !text.Contains('&'))
        {
            return text;
        }

        var stripped = Tags().Replace(text, " ");
        stripped = WebUtility.HtmlDecode(stripped);
        return Runs().Replace(stripped, " ").Trim();
    }
}
