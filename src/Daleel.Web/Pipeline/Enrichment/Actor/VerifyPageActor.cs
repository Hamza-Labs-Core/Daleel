using System.Text;
using System.Text.Json;
using Daleel.Agent;
using Daleel.Core.Models;

namespace Daleel.Web.Pipeline.Enrichment.Actor;

/// <summary>
/// VerifyPage as an LLM ACTOR — a SINGLE turn, no tools (the page is already fetched and handed in).
/// It replaces the densest cluster of fragile heuristics (<c>IsRelatedPage</c> token counting, which
/// can DELETE a real offer on a miss; <c>PickPrice</c> line scoring; <c>ExtractCondition</c>) with one
/// judgment: for each candidate model — is this page really about it? what is its price here? — plus
/// the page's condition evidence and the product's own description. Returns exactly the shapes the
/// deterministic apply already consumes, so it is a drop-in swap behind the <c>actor.verifypage</c> flag.
/// </summary>
public sealed class VerifyPageActor
{
    private static readonly ActorBounds Bounds = new(MaxTurns: 1, MaxToolCalls: 0);
    private const int MaxPageChars = 8000;

    private readonly IActorLoop _loop;

    public VerifyPageActor(IActorLoop loop) => _loop = loop;

    public sealed record PageJudgment(
        HashSet<string> Related,
        Dictionary<string, (decimal Price, string Currency, bool Exact)?> PricesByModel,
        string? Condition,
        string? Description);

    public async Task<PageJudgment?> JudgeAsync(
        AgentService agent, string pageContent, IReadOnlyList<ProductModel> named, CancellationToken ct)
    {
        var names = named.Select(m => m.Name).ToList();
        var system =
            "ROLE: You verify a store/offer page against a short list of product models.\n" +
            "For EACH listed model decide: is this page actually selling or describing THAT model " +
            "(not an accessory, a different model, or an unrelated product)? If yes, read its price ON " +
            "THIS PAGE. Also read the page's condition evidence and the product's own description prose " +
            "(ignore navigation/menus).\n" +
            "Use ONLY the page content; never invent a price or spec.\n" +
            "OUTPUT (done result): {\"models\":[{\"name\":\"<exact name from the list>\",\"related\":true|false," +
            "\"price\":{\"value\":<number>,\"currency\":\"<ISO or symbol>\",\"exact\":true|false}|null}], " +
            "\"condition\":\"new|used|refurbished\"|null, \"description\":\"<product prose>\"|null}. " +
            "'exact' is true for a firm listed price, false for an approximate/indicative figure.";

        var context = new StringBuilder();
        context.Append("MODELS TO CHECK:\n");
        foreach (var n in names) context.Append("- ").Append(n).Append('\n');
        context.Append("\nPAGE CONTENT:\n").Append(Clip(pageContent, MaxPageChars));

        var res = await _loop.RunAsync(
            agent, system, context.ToString(), Array.Empty<ActorTool>(), NoTools, Bounds, ct);
        return res.Completed && res.Result is { } r ? Parse(r, named) : null;
    }

    private static Task<string> NoTools(string tool, JsonElement args, CancellationToken ct) =>
        Task.FromResult("no tools available — return your done result");

    private static PageJudgment Parse(JsonElement r, IReadOnlyList<ProductModel> named)
    {
        var related = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prices = new Dictionary<string, (decimal, string, bool)?>(StringComparer.OrdinalIgnoreCase);

        if (r.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in models.EnumerateArray())
            {
                var name = m.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                    ? nm.GetString() : null;
                // Bind the returned name back to a real candidate (case-insensitive) — ignore stray names.
                var match = name is null ? null : named.FirstOrDefault(
                    x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))?.Name;
                if (match is null)
                {
                    continue;
                }

                if (!(m.TryGetProperty("related", out var rel) && rel.ValueKind == JsonValueKind.True))
                {
                    continue; // not related ⇒ not in the set (an unrelated offer will be dropped by apply)
                }

                related.Add(match);

                if (m.TryGetProperty("price", out var pr) && pr.ValueKind == JsonValueKind.Object &&
                    pr.TryGetProperty("value", out var pv) && pv.TryGetDecimal(out var value) && value > 0)
                {
                    var currency = pr.TryGetProperty("currency", out var cur) && cur.ValueKind == JsonValueKind.String
                        ? cur.GetString() ?? string.Empty : string.Empty;
                    var exact = pr.TryGetProperty("exact", out var ex) && ex.ValueKind == JsonValueKind.True;
                    if (!string.IsNullOrWhiteSpace(currency))
                    {
                        prices[match] = (value, currency, exact);
                    }
                }
            }
        }

        var condition = r.TryGetProperty("condition", out var c) && c.ValueKind == JsonValueKind.String
            ? Normalize(c.GetString()) : null;
        var description = r.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String &&
                          !string.IsNullOrWhiteSpace(d.GetString())
            ? d.GetString() : null;

        return new PageJudgment(related, prices, condition, description);
    }

    private static string? Normalize(string? s)
    {
        var v = s?.Trim().ToLowerInvariant();
        return v is "new" or "used" or "refurbished" ? v : null;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}
