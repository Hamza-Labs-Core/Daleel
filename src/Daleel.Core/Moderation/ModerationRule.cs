namespace Daleel.Core.Moderation;

/// <summary>
/// One dynamic adjustment to the keyword layer, maintained as DATA (not code) so the filter can
/// learn without a deploy: the LLM auto-reviewer proposes them from finding audits, admins can
/// create or revoke them, and every search run consumes the active set via the policy snapshot.
/// </summary>
/// <param name="Kind"><see cref="SuppressTerm"/> or <see cref="AddTerm"/>.</param>
/// <param name="Category">The blocklist category the rule touches, e.g. "alcohol".</param>
/// <param name="Term">The trigger term (for suppressions: as matched, prefix tolerated).</param>
/// <param name="Language">"en" or "ar".</param>
public sealed record ModerationRule(string Kind, string Category, string Term, string Language)
{
    public const string SuppressTerm = "suppress-term";
    public const string AddTerm = "add-term";
}
