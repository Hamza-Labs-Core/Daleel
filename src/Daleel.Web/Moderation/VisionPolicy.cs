using System.Text;
using Daleel.Core.Moderation;

namespace Daleel.Web.Moderation;

/// <summary>
/// The halal image-moderation POLICY expressed as a LIST of rules, plus the composition of those rules
/// into the vision model's system prompt. The rules are the editable policy (managed on /admin/moderation
/// as a list and persisted as <c>ImageModerationRule</c> rows); this class holds the built-in defaults and
/// the fixed framing that wraps them ("the following rules apply … respond as JSON"). Single source of
/// truth: the classifier's default prompt is <see cref="Compose"/> over <see cref="DefaultRules"/>.
/// </summary>
public static class VisionPolicy
{
    /// <summary>One policy rule: a short category tag and the instruction the model applies for it.</summary>
    public sealed record Rule(string Category, string Instruction);

    /// <summary>The built-in policy — used to seed the DB and as the fallback when no rules are active.</summary>
    public static readonly IReadOnlyList<Rule> DefaultRules = new[]
    {
        new Rule("alcohol", "Alcoholic drinks or bottles, bars, or pubs."),
        new Rule("pork", "Pork products (bacon, ham, and similar)."),
        new Rule("gambling", "Gambling — casinos, betting, or cards/roulette/slots used for wagering."),
        new Rule("adult", "Nudity or sexualized / erotic content."),
        new Rule("drugs", "Recreational drugs or drug paraphernalia."),
        new Rule("tobacco", "Smoking or tobacco products — cigarettes, cigars, vapes, shisha/hookah."),
        new Rule("immodest",
            "A real, living WOMAN who is visible and is NOT in full hijab — her hair, neck, arms, or legs " +
            "are not fully covered, OR she wears tight, sheer, or otherwise form-fitting / revealing " +
            "clothing. This rule applies ONLY when an actual person is visible: a photo with NO person is " +
            "NEVER a match — product-only shots, clothing laid flat or on a hanger, a garment on a " +
            "MANNEQUIN or a headless/faceless dress form, folded items, accessories, shoes, bags, " +
            "electronics, food, and logos are all fine. Do NOT flag men, children, or women in full hijab."),
    };

    /// <summary>
    /// Composes the vision system prompt from the active rules: a fixed preamble, the numbered rule list
    /// ("the following rules apply"), the general when-unsure guidance, and the fixed JSON output contract
    /// the parser depends on. Falls back to <see cref="DefaultRules"/> when the list is empty.
    /// </summary>
    public static string Compose(IReadOnlyList<Rule> rules)
    {
        // Never-filtered categories (riba/finance) must not even be described to the model — the
        // "riba is never filtered" invariant, enforced here as well as in the parser's allowed-set.
        var active = (rules is { Count: > 0 } ? rules : DefaultRules)
            .Where(r => !HalalPolicy.NeverFiltered.Contains(r.Category?.Trim().ToLowerInvariant() ?? string.Empty))
            .ToList();
        if (active.Count == 0)
        {
            active = DefaultRules.ToList();
        }

        var sb = new StringBuilder();
        sb.Append(
            "You are a halal-content image moderator for a Muslim shopping assistant. You judge each " +
            "image INDIVIDUALLY. The following rules apply — FLAG an image when it matches ANY rule, and " +
            "tag it with that rule's category:\n\n");
        for (var i = 0; i < active.Count; i++)
        {
            sb.Append(i + 1).Append(". [").Append(active[i].Category).Append("] ")
              .Append(active[i].Instruction).Append('\n');
        }
        sb.Append(
            "\nWhen no rule clearly applies — especially for a photo with no visible person — do NOT flag. " +
            "When you are genuinely unsure about a visible person's modesty, prefer to flag.\n\n" +
            "The images are numbered in the order given. Respond with ONLY a JSON array containing one " +
            "object PER FLAGGED image (omit clean images): {\"index\": number starting at 0, \"category\": " +
            "string (the matching rule's category), \"confidence\": number 0-1, \"reason\": short string}.");
        return sb.ToString();
    }

    /// <summary>
    /// The categories the parser must accept for a set of active rules: the built-in halal categories
    /// UNION the rules' own categories, minus the never-filtered set (riba/finance is never a match).
    /// So an admin-added rule with a new category is honoured, not silently dropped by the parser.
    /// </summary>
    public static IReadOnlySet<string> AllowedCategories(IReadOnlyList<Rule> rules)
    {
        var set = new HashSet<string>(HalalPolicy.AllowedCategories, StringComparer.OrdinalIgnoreCase);
        foreach (var r in rules is { Count: > 0 } ? rules : DefaultRules)
        {
            if (!string.IsNullOrWhiteSpace(r.Category))
            {
                set.Add(r.Category.Trim().ToLowerInvariant());
            }
        }
        set.ExceptWith(HalalPolicy.NeverFiltered);
        return set;
    }
}
