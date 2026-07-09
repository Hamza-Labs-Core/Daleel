using System.Text.Json;
using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Models;

namespace Daleel.Web.Pipeline.Enrichment.Actor;

/// <summary>
/// Store-site discovery as an LLM ACTOR: instead of GuessDomain (strip the name's punctuation and
/// append ".com", then scrape that one URL blindly), the LLM searches and READS to find and CONFIRM
/// the store's OWN official website — catching rebranded/abbreviated domains a slug can't. Returns the
/// verified URL (fed to the researcher as a scrape hint) or null. Bounded (≤3 turns / ≤4 tool calls).
/// </summary>
public sealed class StoreSiteActor
{
    private static readonly ActorBounds Bounds = new(MaxTurns: 3, MaxToolCalls: 4);

    private readonly IActorLoop _loop;

    public StoreSiteActor(IActorLoop loop) => _loop = loop;

    public async Task<string?> FindSiteAsync(
        AgentService agent, string storeName, GeoProfile geo, CancellationToken ct)
    {
        var tools = new[]
        {
            new ActorTool("web_search", "{\"query\":\"...\"} — search the web; returns titles, urls, snippets"),
            new ActorTool("fetch_page", "{\"url\":\"...\"} — open a page to confirm it is the store's own site"),
        };

        var system =
            "ROLE: You find the OFFICIAL website of a retail store/seller in " + geo.Country + ".\n" +
            "GOAL: identify the store's OWN domain (its storefront), NOT a marketplace listing, a " +
            "directory, or a social page ABOUT the store. Open a candidate if you are unsure it is the " +
            "store's own site. If the store has no own website, return null.\n" +
            "OUTPUT (done result): {\"website\": \"<the store's own url>\" | null}.";

        var context = "Store: " + storeName + "\nMarket: " + geo.Country + " (" + geo.CountryCode + ")";

        ActorToolDispatch dispatch = async (tool, args, c) =>
        {
            if (string.Equals(tool, "web_search", StringComparison.OrdinalIgnoreCase))
            {
                var q = Str(args, "query");
                if (string.IsNullOrWhiteSpace(q)) return "provide a non-empty 'query'";
                var bundle = await agent.GatherAsync(new SearchStrategy { WebQueries = new[] { q! } }, geo, c);
                return bundle.WebResults.Count == 0
                    ? "no results"
                    : string.Join("\n", bundle.WebResults.Take(8)
                        .Select(r => "- " + r.Title + (string.IsNullOrWhiteSpace(r.Url) ? "" : " <" + r.Url + ">")));
            }

            if (string.Equals(tool, "fetch_page", StringComparison.OrdinalIgnoreCase))
            {
                var url = Str(args, "url");
                if (string.IsNullOrWhiteSpace(url)) return "provide a non-empty 'url'";
                var page = await agent.ReadPageAsync(url!, c);
                return string.IsNullOrWhiteSpace(page?.Content) ? "the page returned no readable content"
                    : (page!.Content!.Length <= 4000 ? page.Content! : page.Content![..4000]);
            }

            return "unknown tool";
        };

        var res = await _loop.RunAsync(agent, system, context, tools, dispatch, Bounds, ct);
        if (!res.Completed || res.Result is not { } r ||
            !r.TryGetProperty("website", out var w) || w.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var site = w.GetString();
        return !string.IsNullOrWhiteSpace(site) &&
               Uri.TryCreate(site, UriKind.Absolute, out var u) &&
               (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? site : null;
    }

    private static string? Str(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
