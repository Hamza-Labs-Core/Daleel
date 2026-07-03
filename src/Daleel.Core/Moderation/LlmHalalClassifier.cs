using System.Text;
using System.Text.Json.Serialization;
using Daleel.Core.Llm;

namespace Daleel.Core.Moderation;

/// <summary>
/// Context-aware halal text classification over <see cref="ILlmClient"/>. Items are batched into
/// few calls (short texts, JSON verdicts) so a whole gather round costs one or two completions.
/// The model adjudicates keyword flags (overturning false positives like "Barber shop" or a
/// hotel "near the Bar district") and adds flags for haram content the keyword list can't see.
/// </summary>
/// <remarks>
/// Failure contract: any parse/transport error returns an empty list — the caller then treats the
/// deterministic keyword decisions as final. Moderation must never fault a search (the same
/// best-effort rule as the cancel-check hot path).
/// </remarks>
public sealed class LlmHalalClassifier : IHalalClassifier
{
    /// <summary>Max items per completion call; keeps prompts small and verdicts reliable.</summary>
    private const int BatchSize = 80;

    /// <summary>Max characters of one item's text sent to the model.</summary>
    private const int MaxTextLength = 280;

    private const string SystemPrompt =
        "You are a halal-content moderator for a Muslim shopping assistant. You judge INDIVIDUAL " +
        "items (product listings, store names, web results, social posts) — never a whole store or " +
        "site. A store that sells electronics alongside some alcohol is fine as a store: only the " +
        "alcohol ITEMS are haram.\n\n" +
        "Flag an item ONLY when the item itself is haram content: alcohol (drinks, bars, liquor " +
        "stores), pork products, gambling, adult/immodest content, recreational drugs, tobacco. " +
        "Use exactly these category names: alcohol, pork, gambling, adult, drugs, tobacco.\n\n" +
        "NEVER flag: banks, loans, interest/riba financing, insurance, or any financial service — " +
        "a store's financing model is not haram content and the user can always pay cash. NEVER " +
        "flag an item merely because the seller also sells haram goods. NEVER flag place names, " +
        "person names, or words that merely resemble a blocked term (barber ≠ bar; Weedon ≠ weed).\n\n" +
        "Some items carry a keyword-filter hint (the term a simple blocklist matched). Re-judge " +
        "those in context and give an explicit verdict — haram true or false — for EVERY hinted item.\n\n" +
        "Calibrate confidence honestly: reserve confidence above 0.8 for items that UNMISTAKABLY " +
        "sell or promote haram content (a liquor store, a pork product listing). Ambiguous names, " +
        "incidental mentions, places merely NEAR something haram, or ordinary products from a " +
        "store that also sells haram goods deserve haram=false or a LOW confidence — hiding a " +
        "legitimate product is a worse error than showing a questionable one.\n\n" +
        "Respond with ONLY a JSON array. One object per haram item, plus one per hinted item even " +
        "when halal: {\"id\": number, \"haram\": boolean, \"category\": string|null, " +
        "\"confidence\": number 0-1, \"reason\": short string}. Omit unhinted halal items.";

    private readonly ILlmClient _llm;

    public LlmHalalClassifier(ILlmClient llm) => _llm = llm ?? throw new ArgumentNullException(nameof(llm));

    public bool IsConfigured => true;

    public async Task<IReadOnlyList<HalalVerdict>> ClassifyAsync(
        IReadOnlyList<HalalCandidate> items, CancellationToken ct = default)
    {
        if (items.Count == 0)
        {
            return Array.Empty<HalalVerdict>();
        }

        var verdicts = new List<HalalVerdict>();
        for (var offset = 0; offset < items.Count; offset += BatchSize)
        {
            var batch = items.Skip(offset).Take(BatchSize).ToList();
            try
            {
                var text = await _llm.CompleteTextAsync(SystemPrompt, BuildPrompt(batch), ct).ConfigureAwait(false);
                verdicts.AddRange(ParseVerdicts(text, batch));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // genuine cancellation (user / cost cap) propagates
            }
            catch
            {
                // Best-effort: a failed batch yields no verdicts; keyword decisions stand for it.
                // Deliberately do not fail other batches — partial adjudication is still useful.
            }
        }

        return verdicts;
    }

    private static string BuildPrompt(IReadOnlyList<HalalCandidate> batch)
    {
        var sb = new StringBuilder(batch.Count * 96);
        sb.AppendLine("Items to judge:");
        foreach (var item in batch)
        {
            sb.Append('#').Append(item.Id).Append(" [").Append(item.Kind).Append("] ");
            if (item.KeywordCategory is not null)
            {
                sb.Append("(keyword hint: ").Append(item.KeywordCategory)
                  .Append(" via \"").Append(item.KeywordRule).Append("\") ");
            }

            sb.AppendLine(Truncate(item.Text));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses and sanitizes the model's verdicts: unknown ids and non-canonical categories are
    /// discarded, and anything in the never-filtered set (riba/banking/…) is dropped outright —
    /// a hard policy backstop that no prompt drift can bypass.
    /// </summary>
    internal static IReadOnlyList<HalalVerdict> ParseVerdicts(string? responseText, IReadOnlyList<HalalCandidate> batch)
    {
        var dtos = LlmJson.Deserialize<List<VerdictDto>>(responseText);
        if (dtos is null)
        {
            return Array.Empty<HalalVerdict>();
        }

        var knownIds = batch.Select(b => b.Id).ToHashSet();
        // Deduped by id: models sometimes emit the same item twice (the prompt's "per haram item,
        // plus per hinted item" clauses both match a hinted-and-haram item). A haram verdict wins
        // over a duplicate halal one — fail-safe toward compliance, and the caller can rely on
        // at most one verdict per id.
        var byId = new Dictionary<int, HalalVerdict>(dtos.Count);
        foreach (var dto in dtos)
        {
            if (!knownIds.Contains(dto.Id))
            {
                continue;
            }

            var category = dto.Category?.Trim().ToLowerInvariant();
            if (dto.Haram)
            {
                // A haram verdict needs a canonical category, and never-filtered categories
                // (riba, banking, finance…) are ignored no matter how confident the model is.
                if (category is null
                    || HalalPolicy.NeverFiltered.Contains(category)
                    || !HalalPolicy.AllowedCategories.Contains(category))
                {
                    continue;
                }
            }

            var verdict = new HalalVerdict(
                dto.Id, dto.Haram, dto.Haram ? category : null,
                Math.Clamp(dto.Confidence, 0.0, 1.0),
                string.IsNullOrWhiteSpace(dto.Reason) ? null : dto.Reason.Trim());

            if (!byId.TryGetValue(dto.Id, out var existing) || (verdict.IsHaram && !existing.IsHaram))
            {
                byId[dto.Id] = verdict;
            }
        }

        return byId.Values.ToList();
    }

    private static string Truncate(string text)
    {
        var t = text.Trim().ReplaceLineEndings(" ");
        return t.Length <= MaxTextLength ? t : t[..MaxTextLength] + "…";
    }

    private sealed class VerdictDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("haram")] public bool Haram { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("confidence")] public double Confidence { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
    }
}
