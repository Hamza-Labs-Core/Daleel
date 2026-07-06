using Daleel.Core.Moderation;

namespace Daleel.Web.Moderation;

/// <summary>Outcome of a query pre-screen: blocked (with the offending haram category) or clear.</summary>
public sealed record PreScreenResult(bool Blocked, string? Category);

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

    private readonly IModerationPolicyProvider _policy;

    public QueryPreScreen(IModerationPolicyProvider policy) => _policy = policy;

    public async Task<PreScreenResult> ScreenAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new PreScreenResult(false, null); // fail-open — empty handled upstream
        }

        try
        {
            var snapshot = await _policy.GetAsync(ct).ConfigureAwait(false);

            // Screen against ONLY the block-set categories, taken from the EFFECTIVE (admin-tunable)
            // category set so suppress/add rules apply. In-process bilingual regex — no LLM, no network.
            var blockCats = snapshot.Categories
                .Where(c => BlockCategories.Contains(c.Name))
                .ToList();
            if (blockCats.Count == 0)
            {
                return new PreScreenResult(false, null);
            }

            var filter = new ContentFilter(FilterStrictness.Strict, whitelist: null, categories: blockCats);
            var hit = filter.MatchDetail(query.Trim());
            return hit is { } h ? new PreScreenResult(true, h.Category) : new PreScreenResult(false, null);
        }
        catch
        {
            return new PreScreenResult(false, null); // fail-open — never block on a policy error
        }
    }
}
