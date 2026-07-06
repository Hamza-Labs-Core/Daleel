using System.Text;
using System.Text.Json;
using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Models;
using Daleel.Search.Abstractions;

namespace Daleel.Web.Pipeline.Enrichment.Actor;

/// <summary>Config flag keys — each converted rung is toggled independently, default off, with the deterministic path preserved.</summary>
public static class ActorFlags
{
    public const string ItemDive = "actor.itemdive";
    public const string VerifyPage = "actor.verifypage";
    public const string Catalog = "actor.catalog";
    public const string BrandResearch = "actor.brandresearch";
}

/// <summary>
/// The item deep-dive as an LLM ACTOR: instead of blindly scraping one URL and dumping raw markdown,
/// the LLM researches the product like a human — picks the authoritative page from the known offers or
/// a search, opens it, confirms it is the same SKU, extracts structured specs, and goes one level
/// deeper only when the page is thin. Bounded (≤5 turns / ≤6 tool calls) inside the unit's cost cap and
/// lease token; every tool call is a metered provider call. Returns the extracted specs, or null when
/// the actor produced nothing usable (the durable unit then simply retries).
/// </summary>
public sealed class ItemDiveActor
{
    private static readonly ActorBounds Bounds = new(MaxTurns: 5, MaxToolCalls: 6);

    private readonly IActorLoop _loop;

    public ItemDiveActor(IActorLoop loop) => _loop = loop;

    public sealed record DiveResult(
        IReadOnlyDictionary<string, string> Specs, string? SourceUrl, bool ConfirmedSku, string Note);

    public async Task<DiveResult?> RunAsync(
        AgentService agent, ProductModel model, string geoKey, CancellationToken ct)
    {
        var geo = GeoProfiles.ResolveOrDefault(geoKey);
        var tools = new[]
        {
            new ActorTool("web_search", "{\"query\":\"...\"} — search the web; returns titles, urls, snippets"),
            new ActorTool("fetch_page", "{\"url\":\"...\"} — open a page and read its text"),
        };

        var system =
            "ROLE: You research ONE product for a " + geo.Country + " shopper.\n" +
            "GOAL: find this product's authoritative specification source, confirm it is the SAME model " +
            "and variant (not an accessory or a different SKU), and extract its structured specifications.\n" +
            "RAILS: research only THIS product; open the fewest pages needed; prefer the official brand or " +
            "product page over a bare reseller listing. Go deeper — a second page, or one " +
            "'{brand} {model} specifications' search — ONLY if the page you read is thin or the identity is " +
            "unconfirmed; otherwise finish. Never invent specs.\n" +
            "OUTPUT (done result): {\"confirmedSku\": true|false, \"specs\": {\"<attribute>\":\"<value>\", ...}, " +
            "\"sourceUrl\": \"<the page you trust>\", \"note\": \"one line on what you found\"}.";

        var context = BuildContext(model);
        ActorToolDispatch dispatch = async (tool, args, c) =>
        {
            if (string.Equals(tool, "web_search", StringComparison.OrdinalIgnoreCase))
            {
                var q = Str(args, "query");
                if (string.IsNullOrWhiteSpace(q))
                {
                    return "provide a non-empty 'query'";
                }

                var bundle = await agent.GatherAsync(new SearchStrategy { WebQueries = new[] { q! } }, geo, c);
                return FormatResults(bundle.WebResults);
            }

            if (string.Equals(tool, "fetch_page", StringComparison.OrdinalIgnoreCase))
            {
                var url = Str(args, "url");
                if (string.IsNullOrWhiteSpace(url))
                {
                    return "provide a non-empty 'url'";
                }

                var page = await agent.ReadPageAsync(url!, c);
                return string.IsNullOrWhiteSpace(page?.Content) ? "the page returned no readable content" : page!.Content!;
            }

            return "unknown tool";
        };

        var res = await _loop.RunAsync(agent, system, context, tools, dispatch, Bounds, ct);
        return res.Completed && res.Result is { } r ? Parse(r) : null;
    }

    private static string BuildContext(ProductModel m)
    {
        var sb = new StringBuilder();
        sb.Append("Product: ").Append(m.Name).Append('\n');
        if (!string.IsNullOrWhiteSpace(m.Brand)) sb.Append("Brand: ").Append(m.Brand).Append('\n');
        if (!string.IsNullOrWhiteSpace(m.Model)) sb.Append("Model: ").Append(m.Model).Append('\n');

        var urls = m.Offers
            .Where(o => !string.IsNullOrWhiteSpace(o.Url))
            .Select(o => o.Url!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
        if (urls.Count > 0)
        {
            sb.Append("Known listing URLs (start here):\n");
            foreach (var u in urls) sb.Append("  - ").Append(u).Append('\n');
        }

        if (m.Specs.Count > 0)
        {
            sb.Append("Specs already known (do not repeat, fill the gaps): ")
              .Append(string.Join(", ", m.Specs.Keys)).Append('\n');
        }

        return sb.ToString();
    }

    private static string FormatResults(IReadOnlyList<SearchResult> results)
    {
        if (results.Count == 0)
        {
            return "no results";
        }

        var sb = new StringBuilder();
        foreach (var r in results.Take(8))
        {
            sb.Append("- ").Append(r.Title);
            if (!string.IsNullOrWhiteSpace(r.Url)) sb.Append(" <").Append(r.Url).Append('>');
            if (!string.IsNullOrWhiteSpace(r.Snippet)) sb.Append(" — ").Append(Clip(r.Snippet, 160));
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static DiveResult Parse(JsonElement r)
    {
        var specs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (r.TryGetProperty("specs", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in s.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                {
                    specs[p.Name] = p.Value.GetString()!;
                }
            }
        }

        var sourceUrl = r.TryGetProperty("sourceUrl", out var su) && su.ValueKind == JsonValueKind.String
            ? su.GetString() : null;
        var confirmed = r.TryGetProperty("confirmedSku", out var cs) && cs.ValueKind == JsonValueKind.True;
        var note = r.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? string.Empty : string.Empty;

        return new DiveResult(specs, sourceUrl, confirmed, note);
    }

    private static string? Str(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}
