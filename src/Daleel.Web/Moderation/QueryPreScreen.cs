using System.Text.RegularExpressions;
using Daleel.Core.Moderation;

namespace Daleel.Web.Moderation;

/// <summary>
/// Outcome of a query pre-screen. <see cref="Blocked"/> = a haram consumable, reject at the door.
/// <see cref="SteeredQuery"/> (when non-null) = the query was a riba/financial PRODUCT request and has
/// been rewritten to steer results to sharia-compliant options (e.g. "best loans" → Islamic financing).
/// </summary>
public sealed record PreScreenResult(bool Blocked, string? Category, string? SteeredQuery = null);

/// <summary>
/// A ZERO-COST gate over the raw query text, run at search SUBMISSION before any provider/LLM spend.
/// It blocks a clearly-haram CONSUMABLE request (alcohol / pork / drugs) so "beer" never costs a
/// cent. It is deliberately NARROW: only those three consumable categories block here; everything else
/// — including riba/financial queries ("best loans" → steered to Islamic options elsewhere), gambling,
/// adult and tobacco — is left to the nuanced result-level moderation. Fail-open: an empty query or a
/// policy-load error never blocks.
/// </summary>
public interface IQueryPreScreen
{
    Task<PreScreenResult> ScreenAsync(string query, CancellationToken ct = default);
}

public sealed class QueryPreScreen : IQueryPreScreen
{
    /// <summary>Consumables that must never be searched for. Gambling/adult/tobacco stay result-level.</summary>
    private static readonly HashSet<string> BlockCategories =
        new(StringComparer.OrdinalIgnoreCase) { "alcohol", "pork", "drugs" };

    /// <summary>
    /// Riba/financial-PRODUCT intent (the user is shopping FOR conventional interest-based finance) —
    /// these are STEERED to sharia-compliant options, never blocked. This is the product-search case,
    /// distinct from a store that merely OFFERS riba installments (never filtered — see ContentFilter).
    /// </summary>
    private static readonly Regex FinanceProduct = new(
        @"\b(loans?|mortgages?|financing|installments?|credit\s*cards?|personal\s*finance|home\s*loan|car\s*loan|قرض|قروض|تمويل|رهن|بطاقة\s*ائتمان)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Already sharia-aware — don't double-steer.</summary>
    private static readonly Regex AlreadyIslamic = new(
        @"\b(islamic|sharia|shariah|halal|murabaha|takaful|إسلامي|إسلامية|شريعة|حلال)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IModerationPolicyProvider _policy;

    public QueryPreScreen(IModerationPolicyProvider policy) => _policy = policy;

    public async Task<PreScreenResult> ScreenAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new PreScreenResult(false, null); // fail-open — empty handled upstream
        }

        var trimmedQuery = query.Trim();

        // (1) Haram-consumable BLOCK takes priority (never steer a blocked query).
        try
        {
            var snapshot = await _policy.GetAsync(ct).ConfigureAwait(false);

            // Screen against ONLY the block-set categories, taken from the EFFECTIVE (admin-tunable)
            // category set so suppress/add rules apply. In-process bilingual regex — no LLM, no network.
            var blockCats = snapshot.Categories
                .Where(c => BlockCategories.Contains(c.Name))
                .ToList();
            if (blockCats.Count > 0)
            {
                var filter = new ContentFilter(FilterStrictness.Strict, whitelist: null, categories: blockCats);
                if (filter.MatchDetail(trimmedQuery) is { } h)
                {
                    return new PreScreenResult(true, h.Category);
                }
            }
        }
        catch
        {
            // fail-open — never block on a policy error; fall through to the (non-blocking) steer.
        }

        // (2) Riba steer: a financial-PRODUCT query is rewritten to sharia-compliant terms so results
        // surface Islamic banks / halal financing. Not a block, and not store-financing filtering.
        if (FinanceProduct.IsMatch(trimmedQuery) && !AlreadyIslamic.IsMatch(trimmedQuery))
        {
            return new PreScreenResult(false, null, trimmedQuery + " islamic sharia-compliant");
        }

        return new PreScreenResult(false, null);
    }
}
