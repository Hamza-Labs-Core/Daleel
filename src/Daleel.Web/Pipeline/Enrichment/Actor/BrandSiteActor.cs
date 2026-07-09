using System.Text;
using System.Text.Json;
using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Models;

namespace Daleel.Web.Pipeline.Enrichment.Actor;

/// <summary>
/// Brand site discovery as an LLM ACTOR: instead of host-substring matching that misses rebranded or
/// abbreviated domains and can't tell an official site from a reseller, the LLM searches and READS
/// candidate pages to pick the brand's real official (global) site, its market-local storefront, a
/// regional variant, and social profiles. Bounded (≤4 turns / ≤5 tool calls) inside the unit's cost
/// cap + lease. Returns the sites it trusts, or null when it found nothing usable.
/// </summary>
public sealed class BrandSiteActor
{
    private static readonly ActorBounds Bounds = new(MaxTurns: 4, MaxToolCalls: 5);

    private readonly IActorLoop _loop;

    public BrandSiteActor(IActorLoop loop) => _loop = loop;

    public sealed record BrandSites(
        string? Website, string? LocalUrl, string? RegionalUrl, string? Description, IReadOnlyList<string> Social);

    public async Task<BrandSites?> FindAsync(
        AgentService agent, string brand, GeoProfile geo, CancellationToken ct)
    {
        var tools = new[]
        {
            new ActorTool("web_search", "{\"query\":\"...\"} — search the web; returns titles, urls, snippets"),
            new ActorTool("fetch_page", "{\"url\":\"...\"} — open a page to confirm it is the brand's own site"),
        };

        var system =
            "ROLE: You find the official web presence of a product brand for a " + geo.Country + " shopper.\n" +
            "GOAL: identify (1) the brand's OFFICIAL global site (the manufacturer's own domain, NOT a " +
            "marketplace or reseller listing about the brand), (2) its market-local storefront for " +
            geo.Country + " if one exists, (3) a regional site if distinct, and (4) official social profiles.\n" +
            "RAILS: confirm a site is the brand's OWN before trusting it — open it if unsure. Prefer the " +
            "manufacturer domain over any reseller. A missing local/regional site is a fine answer (null). " +
            "Do not invent URLs.\n" +
            "OUTPUT (done result): {\"website\":\"<official global url>\"|null, \"localUrl\":\"<" + geo.Country +
            " site>\"|null, \"regionalUrl\":\"<regional site>\"|null, \"description\":\"<one line about the " +
            "brand from its site>\"|null, \"social\":[\"<profile url>\", ...]}.";

        var context = "Brand: " + brand + "\nMarket: " + geo.Country + " (" + geo.CountryCode + ")";

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
                if (bundle.WebResults.Count == 0)
                {
                    return "no results";
                }

                var sb = new StringBuilder();
                foreach (var r in bundle.WebResults.Take(8))
                {
                    sb.Append("- ").Append(r.Title);
                    if (!string.IsNullOrWhiteSpace(r.Url)) sb.Append(" <").Append(r.Url).Append('>');
                    sb.Append('\n');
                }

                return sb.ToString();
            }

            if (string.Equals(tool, "fetch_page", StringComparison.OrdinalIgnoreCase))
            {
                var url = Str(args, "url");
                if (string.IsNullOrWhiteSpace(url))
                {
                    return "provide a non-empty 'url'";
                }

                var page = await agent.ReadPageAsync(url!, c);
                return string.IsNullOrWhiteSpace(page?.Content)
                    ? "the page returned no readable content"
                    : (page!.Content!.Length <= 4000 ? page.Content! : page.Content![..4000]);
            }

            return "unknown tool";
        };

        var res = await _loop.RunAsync(agent, system, context, tools, dispatch, Bounds, ct);
        return res.Completed && res.Result is { } r ? Parse(r) : null;
    }

    private static BrandSites Parse(JsonElement r)
    {
        var social = new List<string>();
        if (r.TryGetProperty("social", out var s) && s.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in s.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String && IsHttpUrl(el.GetString()))
                {
                    social.Add(el.GetString()!);
                }
            }
        }

        return new BrandSites(
            Url(r, "website"), Url(r, "localUrl"), Url(r, "regionalUrl"), Text(r, "description"), social);
    }

    private static string? Url(JsonElement r, string key) =>
        r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && IsHttpUrl(v.GetString())
            ? v.GetString() : null;

    private static string? Text(JsonElement r, string key) =>
        r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString() : null;

    private static bool IsHttpUrl(string? s) =>
        !string.IsNullOrWhiteSpace(s) && Uri.TryCreate(s, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static string? Str(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
