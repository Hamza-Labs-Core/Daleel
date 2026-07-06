using System.Text;
using System.Text.Json;
using Daleel.Agent;

namespace Daleel.Web.Pipeline.Enrichment.Actor;

/// <summary>
/// CatalogAttach relatedness as an LLM ACTOR — a SINGLE turn, no tools. When a store-catalogue crawl
/// discovers NEW products, the token-overlap match can admit accessories and unrelated categories (its
/// own comment cites a fondant spray gun surfacing on an espresso search). This filters the discovered
/// names against the shopper's query, keeping only genuine same-kind products. Fail-open: an empty
/// list, a parse failure, or an actor error keeps everything (never strips the catalogue wholesale).
/// </summary>
public sealed class CatalogActor
{
    private static readonly ActorBounds Bounds = new(MaxTurns: 1, MaxToolCalls: 0);

    private readonly IActorLoop _loop;

    public CatalogActor(IActorLoop loop) => _loop = loop;

    /// <summary>Returns the subset of <paramref name="names"/> that are genuinely related to the query.</summary>
    public async Task<HashSet<string>> FilterRelatedAsync(
        AgentService agent, string query, IReadOnlyList<string> names, CancellationToken ct)
    {
        var all = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (all.Count == 0)
        {
            return all;
        }

        const string system =
            "You filter a store catalogue's newly-discovered products against a shopper's search. " +
            "Keep ONLY products that are the SAME KIND of product the query is looking for; REJECT " +
            "accessories, spare parts, add-ons, and unrelated categories (e.g. a spray gun on an espresso " +
            "search). Use ONLY the given names. Return STRICT JSON: {\"related\":[\"<exact name>\", ...]}.";

        var context = new StringBuilder("Query: ").Append(query).Append("\nCandidate products:\n");
        foreach (var n in names) context.Append("- ").Append(n).Append('\n');

        var res = await _loop.RunAsync(agent, system, context.ToString(), Array.Empty<ActorTool>(), NoTools, Bounds, ct);
        if (!res.Completed || res.Result is not { } r ||
            !r.TryGetProperty("related", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return all; // fail-open — keep everything rather than strip on a bad/empty response
        }

        var keep = arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
            .Select(e => e.GetString()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Bind back to real candidates only (ignore stray names the model invented). If the model
        // returned NOTHING usable, treat as fail-open and keep all.
        var related = all.Where(n => keep.Contains(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return related.Count > 0 ? related : all;
    }

    private static Task<string> NoTools(string tool, JsonElement args, CancellationToken ct) =>
        Task.FromResult("no tools available — return your done result");
}
